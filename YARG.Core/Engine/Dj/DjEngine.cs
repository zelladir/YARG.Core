using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Chart;
using YARG.Core.Input;

namespace YARG.Core.Engine.Dj
{
    /// <summary>
    /// Headless policy-1 DJ engine. Judgments at time t are finalized once time advances past t,
    /// allowing every control in a timestamped report to be applied atomically, independent of order.
    /// Scoring integrates input intervals and chart subdivisions, never render frames.
    /// </summary>
    public sealed class DjEngine : BaseEngine<DjNote, DjEngineParameters, DjStats>
    {
        private sealed class ObjectState
        {
            public DjNote Note = null!;
            public int Group;
            public bool Entered, Returned, Resolved, FailedCoverage;
            public int ReturnFade;
            public double ReturnOpen, ReturnClose;
        }
        private sealed class Coverage
        {
            public ObjectState? Owner;
            public Phrase? Effect;
            public double Start, End, BucketStart, Good, BadSince = double.NaN, Travel;
            public bool HasGood;
        }
        private sealed class Scheduled
        {
            public double Time;
            public int Order;
            public Action Action = null!;
        }

        private readonly List<ObjectState> _objects = new();
        private readonly List<Coverage> _coverage = new();
        private readonly List<Scheduled> _events = new();
        private readonly Dictionary<int, GameInput> _pending = new();
        private bool[] _groupFinished = Array.Empty<bool>(), _groupBroken = Array.Empty<bool>();
        private int _eventIndex, _buttons, _scratchDirection;
        private float _velocity, _dial;
        private bool _hasDial, _euphoriaButton;
        private double _pendingTime = double.NaN, _time = double.MinValue;
        private double _euphoriaEnd = double.PositiveInfinity;
        private bool _initialized;
        private readonly bool _calculateReference;

        public int CrossfaderPosition { get; private set; } // -1 green, 0 center, +1 blue
        public int HeldButtons => _buttons;
        /// <summary>Logical input/chart timestamp during note, combo and phrase callbacks.</summary>
        public double JudgmentTime => _time;

        public DjEngine(InstrumentDifficulty<DjNote> chart, SyncTrack syncTrack,
            DjEngineParameters parameters, bool isBot = false)
            : this(chart, syncTrack, parameters, isBot, true)
        {
        }

        private DjEngine(InstrumentDifficulty<DjNote> chart, SyncTrack syncTrack,
            DjEngineParameters parameters, bool isBot, bool calculateReference)
            : base(chart, syncTrack, parameters, true, isBot)
        {
            if (syncTrack.Resolution < parameters.Subdivisions || syncTrack.Tempos.Count == 0 ||
                syncTrack.TimeSignatures.Count == 0) throw new ArgumentException("DJ needs a populated tempo map.");
            _initialized = true;
            _calculateReference = calculateReference;
            Reset();
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            base.Reset(keepCurrentButtons);
            // The generic base calls Reset during its constructor, before our field initializers.
            if (!_initialized) return;
            _objects.Clear(); _coverage.Clear(); _events.Clear(); _pending.Clear();
            _eventIndex = 0; _time = double.MinValue; _pendingTime = double.NaN;
            _scratchDirection = 0; _velocity = 0; _dial = 0; _hasDial = false; _euphoriaButton = false;
            if (!keepCurrentButtons) { _buttons = 0; CrossfaderPosition = 0; }
            _euphoriaEnd = double.PositiveInfinity;
            BaseTimeInStarPower = 0; StarPowerActivationTime = StarPowerEndTime = 0;
            StarPowerTickPosition = PreviousStarPowerTickPosition = StarPowerTickActivationPosition = StarPowerTickEndPosition = 0;
            EngineStats.CoverageUnitsTotal = EngineStats.EffectsUnitsTotal = 0;
            BuildTimeline();
            if (_calculateReference)
            {
                // Use the same scoring timeline for the non-euphoria FC reference, including combo ramp,
                // partial coverage units and effects. The clone keeps reference play out of caller note state.
                var reference = new DjEngine(Chart.Clone(), SyncTrack, EngineParameters, true, false);
                reference.Update(_events.Count == 0 ? 0 : _events[_events.Count - 1].Time + 1);
                BaseScore = reference.EngineStats.TotalScore;
                StarScoreThresholds = PopulateStarScoreThresholds(EngineParameters.StarMultiplierThresholds,
                    EngineParameters.SoloBonusStarMultiplierThresholds, BaseScore, 0);
            }
        }

        public override void SetSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0 || speed > 10)
                throw new ArgumentOutOfRangeException(nameof(speed));
            if (CurrentTime != double.MinValue) throw new InvalidOperationException("Reset before changing DJ speed.");
            base.SetSpeed(speed);
            if (_initialized) Reset();
        }

        public override void ReplaceChart(InstrumentDifficulty<DjNote> chart) =>
            throw new NotSupportedException("Create a new DJ engine and replay input history when changing difficulty.");

        private void Schedule(double time, Action action)
        {
            if (_events.Count >= 2000000) throw new ArgumentException("DJ chart exceeds the scoring-event limit.");
            _events.Add(new Scheduled { Time = time, Order = _events.Count, Action = action });
        }

        private void BuildTimeline()
        {
            _groupFinished = new bool[Notes.Count]; _groupBroken = new bool[Notes.Count];
            for (int i = 0; i < Notes.Count; i++)
            {
                foreach (var note in new[] { Notes[i] }.Concat(Notes[i].ChildNotes).OrderBy(n => n.Type))
                {
                    if (note.IsFreestyle) continue;
                    if (note.Type == DjNoteType.OpenTap)
                        throw new NotSupportedException("Open taps need a separately defined input rule.");
                    if (!Finite(note.Time) || !Finite(note.TimeEnd) || note.TimeEnd < note.Time || note.TickEnd < note.Tick)
                        throw new ArgumentException("Invalid DJ event timing.");
                    _objects.Add(new ObjectState { Note = note, Group = i });
                }
            }
            foreach (var group in _objects.Where(o => o.Note.IsCrossfade).GroupBy(o => o.Note.Tick))
                if (group.Count() > 1) throw new ArgumentException("Conflicting simultaneous crossfades.");

            var euphoria = Chart.Phrases.Where(p => p.Type == PhraseType.StarPower).OrderBy(p => p.Time).ToArray();
            for (int i = 1; i < euphoria.Length; i++)
                if (euphoria[i].Time < euphoria[i - 1].TimeEnd) throw new ArgumentException("Overlapping euphoria phrases.");

            foreach (var obj in _objects)
            {
                var note = obj.Note;
                var (front, back) = Window();
                if (note.IsSpike)
                {
                    // Split regular-fade coverage where a spike temporarily overrides its target.
                    Schedule(note.Time, () => { });
                    Schedule(note.TimeEnd, () => { });
                    obj.ReturnFade = _objects.LastOrDefault(o => o.Note.IsCrossfade && !o.Note.IsSpike && o.Note.Time < note.Time) is { } prior
                        ? Fade(prior.Note) : 0;
                    var next = _objects.FirstOrDefault(o => o.Note.IsCrossfade && o.Note.Time > note.Time);
                    // Entry and return windows do not borrow the following fade/spike's time.
                    obj.ReturnOpen = Math.Max(note.Time, note.TimeEnd + front);
                    obj.ReturnClose = Math.Min(note.TimeEnd + back, next?.Note.Time ?? double.PositiveInfinity);
                    obj.ReturnOpen = Math.Min(obj.ReturnOpen, obj.ReturnClose);
                    Schedule(obj.ReturnOpen, () => TryFades(_time, false));
                    Schedule(obj.ReturnClose, () => { TryFades(_time, false); Resolve(obj, obj.Entered && obj.Returned, _time); });
                }
                if (note.IsCrossfade)
                    Schedule(note.Time + front, () => TryFades(_time, false));
                if (!note.IsScratchZone)
                {
                    double deadline = note.IsSpike ? Math.Min(note.Time + back, obj.ReturnClose) : note.Time + back;
                    Schedule(deadline, () =>
                    {
                        TryFades(_time, false);
                        if (!obj.Entered) Resolve(obj, false, _time);
                    });
                }
                if (IsBot && !note.IsScratchZone)
                    Schedule(note.Time, () => { obj.Entered = true; if (!note.IsSpike) Resolve(obj, true, note.Time); });
                if (note.IsScratchZone || (note.IsCrossfade && !note.IsSpike && note.TickLength > 0))
                    AddCoverage(obj, null, note.Tick, note.TickEnd);
            }

            foreach (var phrase in Chart.Phrases.Where(p => p.Type == PhraseType.DjCrossfadeNull))
            {
                Schedule(phrase.Time, () => { });
                Schedule(phrase.TimeEnd, () => { });
            }

            // Reject ambiguous overlapping budgets instead of awarding multiple bonuses for one dial motion.
            var effects = Chart.Phrases.Where(IsEffect).OrderBy(p => p.Tick).ToArray();
            foreach (var phrase in effects)
            {
                if (effects.Any(p => !ReferenceEquals(p, phrase) && p.Tick < phrase.TickEnd && p.TickEnd > phrase.Tick))
                    throw new ArgumentException("Overlapping effects phrases must be merged before gameplay.");
                AddCoverage(null, phrase, phrase.Tick, phrase.TickEnd);
            }
            foreach (var phrase in euphoria)
            {
                var members = _objects.Where(o => o.Note.Time >= phrase.Time && o.Note.Time < phrase.TimeEnd).ToArray();
                double finish = Math.Max(phrase.TimeEnd, members.Select(o => o.Note.IsSpike ? o.ReturnClose : o.Note.IsScratchZone || o.Note.IsCrossfade
                    ? Math.Max(o.Note.TimeEnd, o.Note.Time + Window().BackEnd)
                    : o.Note.Time + Window().BackEnd).DefaultIfEmpty(phrase.TimeEnd).Max());
                Schedule(finish, () =>
                {
                    if (!StarPowerIsAllowed || members.Length == 0) return;
                    if (members.Any(o => !o.Note.WasHit || o.FailedCoverage))
                    {
                        OnStarPowerPhraseMissed?.Invoke(members[0].Note);
                        return;
                    }
                    EngineStats.StarPowerPhrasesHit++;
                    DrainTo(_time);
                    bool ready = CanStarPowerActivate;
                    EngineStats.StarPowerTickAmount = Math.Min(TicksPerFullSpBar, EngineStats.StarPowerTickAmount + TicksPerQuarterSpBar);
                    EngineStats.TotalStarPowerTicks += TicksPerQuarterSpBar;
                    EngineStats.TotalStarPowerBarsFilled = (double) EngineStats.TotalStarPowerTicks / TicksPerFullSpBar;
                    if (!ready && CanStarPowerActivate) OnStarPowerReady?.Invoke();
                    if (EngineStats.IsStarPowerActive) SetEuphoriaEnd();
                    OnStarPowerPhraseHit?.Invoke(members[0].Note);
                });
            }
            _events.Sort((a, b) => { int c = a.Time.CompareTo(b.Time); return c != 0 ? c : a.Order.CompareTo(b.Order); });
            EngineStats.TotalNotes = _objects.Count;
            EngineStats.TotalChords = _objects.Select(o => o.Group).Distinct().Count();
            EngineStats.TotalStarPowerPhrases = euphoria.Length;
            (BaseScore, BaseNoteScore) = CalculateChartScores();
            StarScoreThresholds = PopulateStarScoreThresholds(EngineParameters.StarMultiplierThresholds,
                EngineParameters.SoloBonusStarMultiplierThresholds, BaseScore, 0);
        }

        private void AddCoverage(ObjectState? owner, Phrase? effect, uint start, uint end)
        {
            if (end < start) throw new ArgumentException("Invalid DJ interval.");
            if (end == start)
            {
                if (owner?.Note.IsScratchZone == true) Schedule(owner.Note.Time, () => Resolve(owner, false, _time));
                return;
            }
            var coverage = new Coverage { Owner = owner, Effect = effect, Start = SyncTrack.TickToTime(start),
                End = SyncTrack.TickToTime(end), BucketStart = SyncTrack.TickToTime(start) };
            _coverage.Add(coverage);
            Schedule(coverage.Start, () => { });
            uint step = SyncTrack.Resolution / (uint) EngineParameters.Subdivisions;
            uint tick = start;
            int previousPoints = 0;
            while (tick < end)
            {
                uint next = (uint) Math.Min((ulong) end, (ulong) tick + step);
                int points = checked((int) ((ulong) (next - start) * (uint) (effect == null
                    ? EngineParameters.HoldPointsPerBeat : EngineParameters.EffectsPointsPerBeat) / SyncTrack.Resolution));
                int award = points - previousPoints;
                double beats = (double) (next - tick) / SyncTrack.Resolution;
                bool last = next == end;
                if (effect == null) EngineStats.CoverageUnitsTotal++; else EngineStats.EffectsUnitsTotal++;
                Schedule(SyncTrack.TickToTime(next), () =>
                {
                    bool success = effect != null
                        ? IsBot || coverage.Travel + 1e-9 >= beats * EngineParameters.EffectsTravelPerBeat
                        : IsBot || coverage.Good + 1e-9 >= (_time - coverage.BucketStart) * EngineParameters.CoverageRequired;
                    if (success)
                    {
                        if (effect == null) EngineStats.CoverageUnitsHit++; else EngineStats.EffectsUnitsHit++;
                        Score(award, false, effect != null);
                    }
                    else if (owner != null)
                    {
                        owner.FailedCoverage = true;
                        BreakGroup(owner.Group);
                    }
                    coverage.Good = coverage.Travel = 0;
                    coverage.BucketStart = _time;
                    if (last && owner?.Note.IsScratchZone == true) Resolve(owner, !owner.FailedCoverage, _time);
                });
                previousPoints = points; tick = next;
            }
        }

        protected override void MutateStateWithInput(GameInput input)
        {
            if (!Finite(input.Time) || input.Action < 0 || input.Action > (int) DjAction.Euphoria ||
                (input.Action >= (int) DjAction.Platter && input.Action <= (int) DjAction.EffectsDial && !DjInputNormalizer.Finite(input.Axis)))
                throw new ArgumentException("Invalid DJ input.");
            Advance(input.Time);
            _pendingTime = input.Time;
            _pending[input.Action] = input; // Last sample for the same control/timestamp wins.
        }

        private void ApplyPending()
        {
            IntegrateTo(_pendingTime);
            int oldButtons = _buttons;
            int oldFade = CrossfaderPosition;
            for (int i = 0; i < 3; i++)
                if (_pending.TryGetValue(i, out var button))
                    _buttons = button.Button ? _buttons | (1 << i) : _buttons & ~(1 << i);
            if (_pending.TryGetValue((int) DjAction.Crossfader, out var fade))
            {
                float axis = Math.Clamp(fade.Axis, -1, 1);
                if (axis <= -EngineParameters.FadeEnter) CrossfaderPosition = -1;
                else if (axis >= EngineParameters.FadeEnter) CrossfaderPosition = 1;
                else if ((CrossfaderPosition == -1 && axis >= -EngineParameters.FadeRelease) ||
                    (CrossfaderPosition == 1 && axis <= EngineParameters.FadeRelease)) CrossfaderPosition = 0;
            }
            if (_pending.TryGetValue((int) DjAction.Euphoria, out var euphoria))
            {
                if (euphoria.Button && !_euphoriaButton && StarPowerIsAllowed && CanStarPowerActivate && !EngineStats.IsStarPowerActive)
                {
                    EngineStats.IsStarPowerActive = true; EngineStats.StarPowerActivationCount++;
                    StarPowerActivationTime = _time; StarPowerTickActivationPosition = SyncTrack.TimeToMeasureTick(_time);
                    SetEuphoriaEnd(); UpdateMultiplier(); OnStarPowerStatus?.Invoke(true);
                }
                _euphoriaButton = euphoria.Button;
            }
            TryFades(_time, oldFade != CrossfaderPosition);
            for (int lane = 0; lane < 3; lane++)
                if ((_buttons & (1 << lane)) != 0 && (oldButtons & (1 << lane)) == 0)
                    TryHit(o => o.Note.IsTap && (int) o.Note.Lane == lane && LaneAllowed(o.Note), _time);
            if (_pending.TryGetValue((int) DjAction.Platter, out var platter))
            {
                _velocity = Math.Clamp(platter.Axis, -128, 128);
                int direction = Math.Sign(_velocity);
                if (Math.Abs(_velocity) <= EngineParameters.ScratchRelease) _scratchDirection = 0;
                else if (Math.Abs(_velocity) >= EngineParameters.ScratchActivate && direction != _scratchDirection)
                {
                    _scratchDirection = direction;
                    // One edge can satisfy a scratch chord, but only one note in each lane.
                    for (int lane = 0; lane < 4; lane++)
                        TryHit(o => o.Note.IsScratch && (int) o.Note.Lane == lane && ButtonHeld(o.Note) &&
                            LaneAllowed(o.Note) && ((int) o.Note.Type >= 12 || ((int) o.Note.Type < 8 ? direction > 0 : direction < 0)), _time);
                }
            }
            if (_pending.TryGetValue((int) DjAction.EffectsDial, out var dial))
            {
                float position = dial.Axis - (float) Math.Floor(dial.Axis);
                if (_hasDial)
                {
                    double delta = position - _dial;
                    delta -= Math.Floor(delta + .5);
                    if (Math.Abs(delta) > 1e-7)
                    {
                        TryHit(o => o.Note.Type == (delta < 0 ? DjNoteType.EffectsLeft : DjNoteType.EffectsRight), _time);
                        foreach (var coverage in _coverage)
                            if (coverage.Effect != null && _time >= coverage.Start && _time < coverage.End && EffectHeld(coverage.Effect))
                                coverage.Travel = Math.Min(EngineParameters.EffectsTravelPerBeat, coverage.Travel + Math.Abs(delta));
                    }
                }
                _dial = position; _hasDial = true;
            }
            _pending.Clear(); _pendingTime = double.NaN;
        }

        private void Advance(double until)
        {
            if (!double.IsNaN(_pendingTime) && _pendingTime < until)
            {
                RunEventsBefore(_pendingTime);
                ApplyPending();
            }
            // A pending report at 'until' remains open for other controls at exactly that time.
            RunEventsBefore(until);
        }

        private void RunEventsBefore(double until)
        {
            while (true)
            {
                double next = _eventIndex < _events.Count ? _events[_eventIndex].Time : double.PositiveInfinity;
                double time = Math.Min(next, _euphoriaEnd);
                if (time >= until) break;
                IntegrateTo(time);
                if (next == time) _events[_eventIndex++].Action();
            }
        }

        private void IntegrateTo(double time)
        {
            if (_time != double.MinValue && time > _time)
            {
                foreach (var coverage in _coverage)
                {
                    double from = Math.Max(_time, coverage.Start), to = Math.Min(time, coverage.End);
                    if (to <= from || coverage.Effect != null) continue;
                    bool good = IsBot || CoverageHeld(coverage.Owner!, from);
                    if (good) { coverage.Good += to - from; coverage.BadSince = double.NaN; coverage.HasGood = true; }
                    else
                    {
                        if (double.IsNaN(coverage.BadSince)) coverage.BadSince = from;
                        if (coverage.HasGood)
                            coverage.Good += Math.Max(0, Math.Min(to, coverage.BadSince + EngineParameters.DropoutGrace * EngineParameters.SongSpeed) - from);
                    }
                }
            }
            _time = time;
            DrainTo(time);
            // Reservoir intervals are end-exclusive, including input exactly at exhaustion.
            if (_euphoriaEnd <= time)
            {
                EngineStats.IsStarPowerActive = false; EngineStats.StarPowerTickAmount = 0;
                BaseTimeInStarPower = EngineStats.TimeInStarPower;
                _euphoriaEnd = double.PositiveInfinity; UpdateMultiplier(); OnStarPowerStatus?.Invoke(false);
            }
        }

        private bool CoverageHeld(ObjectState obj, double time)
        {
            if (obj.Note.IsScratchZone) return ButtonHeld(obj.Note) && LaneAllowed(obj.Note) && Math.Abs(_velocity) >= EngineParameters.ScratchActivate;
            return AutoFade(time) || _objects.Any(o => o.Note.IsSpike && time >= o.Note.Time && time < o.Note.TimeEnd)
                || CrossfaderPosition == Fade(obj.Note);
        }
        private bool AutoFade(double time) => EngineStats.IsStarPowerActive && EngineParameters.AutoFadeEuphoria ||
            Chart.Phrases.Any(p => p.Type == PhraseType.DjCrossfadeNull && time >= p.Time && time < p.TimeEnd);
        private bool LaneAllowed(DjNote note) => !EngineParameters.GateOuterLanes || note.Lane is DjLane.Red or DjLane.Open || AutoFade(_time) ||
            CrossfaderPosition == 0 || CrossfaderPosition == (note.Lane == DjLane.Green ? -1 : 1);
        private bool ButtonHeld(DjNote note) => note.Lane == DjLane.Open ? _buttons == 0 : (_buttons & (1 << (int) note.Lane)) != 0;
        private bool EffectHeld(Phrase phrase) => phrase.Type == PhraseType.DjEffectAll ||
            (_buttons & (1 << (phrase.Type == PhraseType.DjEffectGreen ? 0 : phrase.Type == PhraseType.DjEffectRed ? 1 : 2))) != 0;
        private static bool IsEffect(Phrase p) => p.Type is >= PhraseType.DjEffectGreen and <= PhraseType.DjEffectAll;
        private static int Fade(DjNote note) => (int) note.Type - (int) DjNoteType.CrossfadeCenter;
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        private void TryFades(double time, bool changed)
        {
            var (front, back) = Window();
            foreach (var obj in _objects.Where(o => o.Note.IsCrossfade && !o.Resolved))
            {
                var note = obj.Note;
                bool auto = IsBot || AutoFade(time);
                if (!obj.Entered && time >= note.Time + front && time <= note.Time + back &&
                    (!note.IsSpike || time <= obj.ReturnClose) && (auto || CrossfaderPosition == Fade(note) && (!note.IsSpike || changed)))
                {
                    obj.Entered = true;
                    if (!note.IsSpike) Resolve(obj, true, time);
                }
                if (note.IsSpike && obj.Entered && time >= obj.ReturnOpen && time <= obj.ReturnClose &&
                    (auto || CrossfaderPosition == obj.ReturnFade))
                {
                    obj.Returned = true;
                    Resolve(obj, true, time);
                }
            }
        }

        private void TryHit(Func<ObjectState, bool> matches, double time)
        {
            var (front, back) = Window();
            var obj = _objects.FirstOrDefault(o => !o.Resolved && time >= o.Note.Time + front && time <= o.Note.Time + back && matches(o));
            if (obj == null) return;
            obj.Entered = true; Resolve(obj, true, time);
        }

        private void Resolve(ObjectState obj, bool hit, double time)
        {
            if (obj.Resolved) return;
            obj.Resolved = true;
            var note = obj.Note;
            if (hit)
            {
                note.SetHitState(true, false);
                EngineStats.IncrementNotesHit(note, note.IsScratchZone ? note.Time : time);
                if (note.IsTap) EngineStats.TapsHit++;
                else if (note.IsScratch) EngineStats.ScratchesHit++;
                else if (note.IsCrossfade) { EngineStats.CrossfadesHit++; if (note.IsSpike) EngineStats.SpikesHit++; }
                else if (!note.IsScratchZone) EngineStats.EffectsCuesHit++;
                if (!note.IsScratchZone) Score(EngineParameters.NotePoints, true, false);
                OnNoteHit?.Invoke(obj.Group, note);
            }
            else
            {
                note.SetMissState(true, false); EngineStats.Misses++;
                BreakGroup(obj.Group); OnNoteMissed?.Invoke(obj.Group, note);
            }
            if (!_groupFinished[obj.Group] && _objects.Where(o => o.Group == obj.Group).All(o => o.Resolved))
            {
                _groupFinished[obj.Group] = true;
                if (!_groupBroken[obj.Group]) { IncrementCombo(); UpdateMultiplier(); }
            }
            NoteIndex = _objects.Where(o => !o.Resolved).Select(o => o.Group).DefaultIfEmpty(Notes.Count).Min();
        }

        private void BreakGroup(int group)
        {
            if (_groupBroken[group]) return;
            _groupBroken[group] = true; ResetCombo(); UpdateMultiplier();
        }
        private void Score(int points, bool note, bool effect)
        {
            int score = checked(points * EngineStats.ScoreMultiplier);
            EngineStats.CommittedScore = checked(EngineStats.CommittedScore + score);
            if (note) EngineStats.NoteScore += points; else EngineStats.SustainScore += points;
            if (effect) EngineStats.EffectsScore += score;
            EngineStats.MultiplierScore += score - points;
            if (EngineStats.IsStarPowerActive) EngineStats.StarPowerScore += score / 2;
            EngineStats.AverageMultiplier = BaseNoteScore == 0 ? 0 : (float) EngineStats.CommittedScore / BaseNoteScore;
            UpdateStars();
        }

        private void SetEuphoriaEnd()
        {
            StarPowerTickPosition = SyncTrack.TimeToMeasureTick(_time);
            StarPowerTickEndPosition = checked(StarPowerTickPosition + EngineStats.StarPowerTickAmount);
            _euphoriaEnd = StarPowerEndTime = SyncTrack.MeasureTickToTime(StarPowerTickEndPosition);
        }
        private void DrainTo(double time)
        {
            if (!EngineStats.IsStarPowerActive) return;
            StarPowerTickPosition = SyncTrack.TimeToMeasureTick(time);
            EngineStats.StarPowerTickAmount = StarPowerTickEndPosition > StarPowerTickPosition ? StarPowerTickEndPosition - StarPowerTickPosition : 0;
            EngineStats.TimeInStarPower = BaseTimeInStarPower + Math.Max(0, Math.Min(time, _euphoriaEnd) - StarPowerActivationTime);
        }

        // DJ owns its event timeline and coverage; generic guitar sustain/lane logic is intentionally bypassed.
        protected override void GenerateQueuedUpdates(double nextTime) { }
        protected override void UpdateStarPower() { }
        protected override void UpdateBot(double time) { }
        protected override void CheckForNoteHit() { }
        protected override bool CanNoteBeHit(DjNote note) => false;
        protected override bool CanSustainHold(DjNote note) => false;
        protected override bool ProximalLaneForgivesInput(int inputNote, DjNote note) => false;
        protected override void AddScore(DjNote note) => Score(EngineParameters.NotePoints, true, false);
        protected override void UpdateTimeVariables(double time)
        {
            if (!Finite(time) || time < CurrentTime) throw new ArgumentOutOfRangeException(nameof(time), "Reset/seek before moving backwards.");
            LastUpdateTime = CurrentTime; LastTick = CurrentTick;
            CurrentTime = time; CurrentTick = SyncTrack.TimeToTick(time);
        }
        protected override void UpdateHitLogic(double time)
        {
            Advance(time);
            DrainTo(time); // Display-only drain from an absolute end position; no coverage integration on frames.
        }
        private (double FrontEnd, double BackEnd) Window() =>
            (EngineParameters.HitWindow.GetFrontEnd(EngineParameters.HitWindow.MaxWindow),
             EngineParameters.HitWindow.GetBackEnd(EngineParameters.HitWindow.MaxWindow));

        protected override (int baseScore, int noteScore) CalculateChartScores()
        {
            long points = 0;
            foreach (var note in Notes.SelectMany(n => new[] { n }.Concat(n.ChildNotes)).Where(n => !n.IsFreestyle))
            {
                if (!note.IsScratchZone) points += EngineParameters.NotePoints;
                if (note.IsScratchZone || note.IsCrossfade && !note.IsSpike)
                    points += (long) note.TickLength * EngineParameters.HoldPointsPerBeat / SyncTrack.Resolution;
            }
            foreach (var p in Chart.Phrases.Where(IsEffect)) points += (long) p.TickLength * EngineParameters.EffectsPointsPerBeat / SyncTrack.Resolution;
            // Constructor-safe temporary budget; Reset replaces BaseScore with the autoplay FC reference.
            return (checked((int) (points * EngineParameters.MaxMultiplier)), checked((int) points));
        }
    }
}
