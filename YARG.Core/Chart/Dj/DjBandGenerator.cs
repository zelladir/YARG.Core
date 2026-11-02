using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using YARG.Core.Audio;
using YARG.Core.IO.Ini;

namespace YARG.Core.Chart
{
    public sealed class DjGenerationResult
    {
        public string Text { get; internal set; } = "";
        public DjSidecarInfo Info { get; internal set; } = new();
        public double DurationSeconds { get; internal set; }
        public IReadOnlyDictionary<Difficulty, int> NoteCounts { get; internal set; } = new Dictionary<Difficulty, int>();
    }

    /// <summary>Deterministic, chart-derived accompaniment. No audio analysis or random choices.</summary>
    public static class DjBandGenerator
    {
        private readonly struct Cue
        {
            public readonly uint Tick, Length;
            public readonly int Code;
            public Cue(uint tick, int code, uint length = 0) { Tick = tick; Code = code; Length = length; }
            public bool Hand => Code < 20;
            public bool Scratch => Code >= 4 && Code < 16;
            public bool Zone => Code >= 16 && Code < 20;
            public bool Fade => Code >= 64 && Code <= 66;
        }

        public static double MinimumGap(Difficulty difficulty) => difficulty switch
        {
            Difficulty.Beginner => .4, Difficulty.Easy => .25, Difficulty.Medium => .18,
            Difficulty.Hard => .12, Difficulty.Expert => .075, _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
        };
        public static double MaximumScratchRate(Difficulty difficulty) => difficulty == Difficulty.Expert ? 8 : difficulty == Difficulty.Hard ? 5 : 0;
        public static int MaximumChord(Difficulty difficulty) => difficulty <= Difficulty.Easy ? 1 : 2;
        public static double MinimumFadeGap(Difficulty difficulty) => difficulty == Difficulty.Expert ? .5 : difficulty == Difficulty.Hard ? 1 : 2;

        /// <summary>Uses Core's MIDI loader and its ini interpretation for drums, HOPOs, sustains and overdrive.</summary>
        public static SongChart LoadSource(string midiPath)
        {
            var settings = ParseSettings.Default_Midi;
            string ini = Path.Combine(Path.GetDirectoryName(midiPath)!, "song.ini");
            if (File.Exists(ini))
            {
                var modifiers = SongIniHandler.ReadSongIniFile(ini);
                if (modifiers.Extract("five_lane_drums", out bool fiveLane)) settings.DrumsType = fiveLane ? DrumsType.FiveLane : DrumsType.FourLane;
                if (modifiers.Extract("hopo_frequency", out long hopo) && hopo > 0) settings.HopoThreshold = hopo;
                if (modifiers.Extract("sustain_cutoff_threshold", out long cutoff)) settings.SustainCutoffThreshold = cutoff;
                if (modifiers.Extract("multiplier_note", out int overdrive) && overdrive == 103) settings.StarPowerNote = overdrive;
            }
            return SongChart.FromFile(settings, midiPath, mergeDjSidecar: false);
        }

        public static DjGenerationResult GenerateFolder(string folder)
        {
            string midi = Path.Combine(folder, "notes.mid");
            string hash = DjSidecar.HashFile(midi);
            var source = LoadSource(midi);
            // The local validation corpus has a filename inventory in place of the audio itself.
            string inventory = Path.Combine(folder, "stems.txt");
            var stems = File.Exists(inventory) ? File.ReadAllLines(inventory) : Directory.GetFiles(folder).Select(Path.GetFileName).Cast<string>();
            var result = Generate(source, hash, stems);
            if (DjSidecar.HashFile(midi) != hash) throw new IOException("Source MIDI changed during generation.");
            return result;
        }

        public static DjGenerationResult Generate(SongChart source, string sourceHash, IEnumerable<string>? stemFiles = null)
        {
            if (source.Resolution < 4 || source.SyncTrack.Tempos.Count == 0) throw new InvalidDataException("Missing or invalid MIDI timebase.");
            var info = new DjSidecarInfo { Generated = true, Version = DjSidecar.GeneratorVersion, SourceHash = sourceHash, Quality = 100 };
            var stems = (stemFiles ?? Array.Empty<string>()).Select(s => Path.GetFileName(s.Trim())).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();
            info.BeatStems = Group(DjStemGroup.Beat); info.MelodicStems = Group(DjStemGroup.Melodic); info.BackingStems = Group(DjStemGroup.Backing);
            string Group(DjStemGroup group) => string.Join(";", stems.Where(s => DjAudioRouting.ClassifyFile(s) == group));

            uint resolution = source.Resolution, end = source.GetLastTick();
            double duration = Math.Max(1, source.SyncTrack.TickToTime(end));
            var taps = new List<Cue>();
            var rapid = new List<Cue>();
            var phrases = new List<Phrase>();
            var drums = Best(source.ProDrums) ?? Best(source.FourLaneDrums) ?? Best(source.FiveLaneDrums);
            if (drums != null)
            {
                phrases.AddRange(drums.Phrases);
                foreach (var n in drums.Notes.SelectMany(n => new[] { n }.Concat(n.ChildNotes)))
                {
                    int lane = n.Pad == 0 || n.Pad == 1 ? 0 : n.Pad == (int) FourLaneDrumPad.GreenCymbal || n.IsAccent ? 1 : 2;
                    taps.Add(new Cue(n.Tick, lane));
                    // Kick is excluded: double-bass runs should not force repeated platter motion.
                    if (n.Pad != 0) rapid.Add(new Cue(n.Tick, 0));
                }
            }
            int rhythmCount = 0;
            foreach (var guitar in source.FiveFretTracks)
            {
                var track = Best(guitar);
                if (track == null) continue;
                phrases.AddRange(track.Phrases);
                bool beat = guitar.Instrument == Instrument.FiveFretBass || guitar.Instrument == Instrument.FiveFretRhythm;
                if (beat) rhythmCount += track.Notes.Count;
                foreach (var n in track.Notes)
                {
                    taps.Add(new Cue(n.Tick, beat ? 0 : 2));
                    if (!beat) rapid.Add(new Cue(n.Tick, 2));
                }
            }
            var proKeys = Best(source.ProKeys);
            if (proKeys != null)
            {
                phrases.AddRange(proKeys.Phrases);
                foreach (var n in proKeys.Notes) taps.Add(new Cue(n.Tick, 2));
            }
            foreach (var part in (source.Vocals.IsEmpty ? source.Harmony : source.Vocals).Parts)
            {
                foreach (var phrase in part.NotePhrases) taps.Add(new Cue(phrase.Tick, 1));
                phrases.AddRange(part.OtherPhrases);
            }
            if (!source.HasAuthoredSections) Penalize(DjQualityFlags.MissingSections, 15);
            foreach (var section in source.Sections.Where(_ => source.HasAuthoredSections)) taps.Add(new Cue(section.Tick, 1));
            if (drums == null && rhythmCount == 0) Penalize(DjQualityFlags.SparseSource, 30);
            if (taps.Count / duration > 14) Penalize(DjQualityFlags.ExtremeDensity, 15);
            var tempos = source.SyncTrack.Tempos;
            if (tempos.Any(t => t.BeatsPerMinute < 45 || t.BeatsPerMinute > 240) ||
                source.SyncTrack.TimeSignatures.Any(t => t.Denominator != 4 || t.Numerator % 2 != 0) ||
                tempos.Skip(1).Where((t, i) => Math.Abs(t.BeatsPerMinute / tempos[i].BeatsPerMinute - 1) > .25).Any())
                Penalize(DjQualityFlags.IrregularTempo, 10);
            if (tempos.Count > Math.Max(8, duration / 4)) Penalize(DjQualityFlags.RubatoTempo, 15);

            uint Quantize(uint tick, uint step) => (uint) Math.Min(end, ((ulong) tick + step / 2) / step * step);
            uint grid = Math.Max(1, resolution / 4);
            var expert = taps.Select(c => new Cue(Quantize(c.Tick, grid), c.Code)).DistinctByCue().ToList();
            var scratches = new List<Cue>();
            foreach (int lane in new[] { 0, 2 })
            {
                var ticks = rapid.Where(c => c.Code == lane).Select(c => c.Tick).Distinct().OrderBy(t => t).ToArray();
                bool up = true;
                for (int i = 1; i + 1 < ticks.Length; i++)
                {
                    if (ticks[i] - ticks[i - 1] > resolution / 4 || ticks[i + 1] - ticks[i] > resolution / 4) continue;
                    scratches.Add(new Cue(Quantize(ticks[i], grid), (up ? 4 : 8) + lane)); up = !up;
                }
            }
            expert.AddRange(scratches);
            var fills = Union(phrases.Where(p => p.Type == PhraseType.DrumFill).Select(p => (p.Tick, p.TickEnd)));
            foreach (var fill in fills)
            {
                uint start = Quantize(fill.Start, grid), finish = Math.Min(fill.End, end);
                if (finish > start && source.SyncTrack.TickToTime(finish) - source.SyncTrack.TickToTime(start) >= .25)
                    expert.Add(new Cue(start, 16, finish - start));
            }
            uint? coda = source.GetCodaEvent()?.Tick;
            var bre = phrases.Where(p => p.Type == PhraseType.BigRockEnding || p.Type == PhraseType.Coda).ToList();
            if (bre.Count > 0) coda = Math.Min(coda ?? uint.MaxValue, bre.Min(p => p.Tick));
            if (coda.HasValue)
            {
                expert.RemoveAll(c => c.Tick >= coda.Value);
                expert = expert.Select(c => c.Zone && (ulong)c.Tick + c.Length > coda.Value ? new Cue(c.Tick, c.Code, coda.Value - c.Tick) : c).ToList();
                if (end > coda.Value) expert.Add(new Cue(coda.Value, 68, end - coda.Value));
            }
            expert = Thin(expert, Difficulty.Expert, source);

            // Regular holds use original section ticks. Keep both decks audible whenever the opposite lane is busy.
            var sections = source.Sections.Where(_ => source.HasAuthoredSections).OrderBy(s => s.Tick).ToList();
            for (int i = 0; i < sections.Count; i++)
            {
                uint start = sections[i].Tick, finish = i + 1 < sections.Count ? sections[i + 1].Tick : end;
                if (coda.HasValue) finish = Math.Min(finish, coda.Value);
                if (finish <= start || finish - start <= resolution / 4) continue;
                string name = sections[i].Name.ToLowerInvariant();
                int code = name.Contains("verse") ? 64 : name.Contains("solo") || name.Contains("bridge") ? 66 : 65;
                int opposite = code == 64 ? 2 : 0;
                var outer = expert.Where(c => c.Hand && c.Code % 4 == opposite && c.Tick >= start && c.Tick < finish).ToArray();
                if (code != 65 && (outer.Any(c => c.Zone) || outer.Zip(outer.Skip(1), (a, b) => b.Tick - a.Tick).Any(g => g < resolution))) code = 65;
                expert.Add(new Cue(start, code, finish - start));
            }
            // The engine suspends regular-hold coverage during spikes and returns to the preceding hold.
            // Require an isolated accent and clearance from every regular-fade entry.
            uint lastSpike = 0;
            foreach (var accent in expert.Where(c => c.Code == 1).ToArray())
            {
                if (accent.Tick < lastSpike + resolution * 8 || expert.Any(c => c.Fade && Math.Abs(source.SyncTrack.TickToTime(c.Tick) - source.SyncTrack.TickToTime(accent.Tick)) < .75)) continue;
                if (expert.Any(c => c.Hand && c.Tick != accent.Tick && Math.Abs(source.SyncTrack.TickToTime(c.Tick) - source.SyncTrack.TickToTime(accent.Tick)) < .3)) continue;
                var prior = expert.Where(c => c.Fade && c.Length > resolution / 4 && c.Tick < accent.Tick).OrderBy(c => c.Tick).LastOrDefault();
                expert.Add(new Cue(accent.Tick, prior.Code == 64 ? 66 : 64, Math.Max(1, resolution / 8))); lastSpike = accent.Tick;
            }
            expert = Thin(expert, Difficulty.Expert, source);
            var tiers = new Dictionary<Difficulty, List<Cue>> { [Difficulty.Expert] = expert };
            foreach (var difficulty in new[] { Difficulty.Hard, Difficulty.Medium, Difficulty.Easy, Difficulty.Beginner })
                tiers.Add(difficulty, Thin(tiers[(Difficulty) ((int) difficulty + 1)], difficulty, source));

            var energy = Union(phrases.Where(p => p.Type == PhraseType.StarPower).Select(p => (p.Tick, p.TickEnd)));
            var effects = Union(phrases.Where(p => p.Type == PhraseType.Solo).Select(p => (p.Tick, p.TickEnd))
                .Concat(sections.Where(s => s.Name.IndexOf("breakdown", StringComparison.OrdinalIgnoreCase) >= 0).Select(s => (s.Tick, s.TickEnd))));
            var counts = tiers.ToDictionary(k => k.Key, v => v.Value.Count(c => c.Code < 68));
            if (counts[Difficulty.Expert] == 0) Penalize(DjQualityFlags.NoPlayableNotes, 100);
            info.Quality = Math.Max(0, info.Quality); info.Hidden = info.Quality < DjSidecar.HiddenThreshold;
            string text = Serialize(source, info, tiers, energy, effects, coda);
            var validation = new SongChart(source.Resolution) { SyncTrack = source.SyncTrack.Clone() };
            validation.Append(source);
            var validated = DjSidecar.Merge(validation, text, sourceHash);
            if (!validated.Accepted && counts[Difficulty.Expert] != 0) Penalize(DjQualityFlags.SyncMismatch, 100);
            if (validated.Accepted && Validate(validation).Count != 0) Penalize(DjQualityFlags.ConstraintViolation, 100);
            info.Quality = Math.Max(0, info.Quality); info.Hidden = info.Quality < DjSidecar.HiddenThreshold;
            text = Serialize(source, info, tiers, energy, effects, coda);
            return new DjGenerationResult { Text = text, Info = info, DurationSeconds = duration, NoteCounts = counts };

            void Penalize(DjQualityFlags flag, int points) { if ((info.Flags & flag) == 0) info.Quality -= points; info.Flags |= flag; }
        }

        private static InstrumentDifficulty<T>? Best<T>(InstrumentTrack<T> track) where T : Note<T>
        {
            foreach (Difficulty d in new[] { Difficulty.Expert, Difficulty.Hard, Difficulty.Medium, Difficulty.Easy })
                if (track.TryGetDifficulty(d, out var t) && t.Notes.Count > 0) return t;
            return null;
        }

        private static IEnumerable<Cue> DistinctByCue(this IEnumerable<Cue> cues) => cues.GroupBy(c => (c.Tick, c.Code)).Select(g => g.OrderByDescending(c => c.Length).First());

        private static List<Cue> Thin(List<Cue> input, Difficulty tier, SongChart source)
        {
            uint step = Math.Max(1, tier == Difficulty.Beginner ? source.Resolution : tier <= Difficulty.Medium ? source.Resolution / 2 : source.Resolution / 4);
            var cues = input.Select(c => tier <= Difficulty.Medium && (c.Scratch || c.Zone) ? new Cue(c.Tick, c.Code % 4) : c)
                .Where(c => !c.Hand || c.Tick % step == 0).DistinctByCue();
            var result = new List<Cue>();
            double previous = double.NegativeInfinity, zoneEnd = double.NegativeInfinity, scratchTime = double.NegativeInfinity, redTime = double.NegativeInfinity, fadeTime = double.NegativeInfinity;
            int fadeIndex = 0;
            foreach (var group in cues.OrderBy(c => c.Tick).ThenBy(c => c.Code).GroupBy(c => c.Tick))
            {
                double time = source.SyncTrack.TickToTime(group.Key);
                var hands = group.Where(c => c.Hand && (c.Code % 4 != 1 || time - redTime >= .75))
                    .OrderByDescending(c => c.Zone).ThenByDescending(c => c.Scratch).ThenBy(c => c.Code).ToList();
                if (time + 1e-8 >= zoneEnd && time - previous + 1e-8 >= MinimumGap(tier) && hands.Count > 0)
                {
                    Cue first = hands[0];
                    if (first.Scratch && time - scratchTime + 1e-8 < 1 / MaximumScratchRate(tier)) first = new Cue(first.Tick, first.Code % 4);
                    result.Add(first);
                    if (first.Scratch) scratchTime = time;
                    if (first.Zone) zoneEnd = source.SyncTrack.TickToTime(first.Tick + first.Length) + MinimumGap(tier);
                    if (first.Code % 4 == 1) redTime = time;
                    if (!first.Zone && !first.Scratch)
                        foreach (var extra in hands.Where(c => !c.Zone && !c.Scratch && c.Code != first.Code).Take(MaximumChord(tier) - 1))
                        { result.Add(extra); if (extra.Code == 1) redTime = time; }
                    previous = time;
                }
                foreach (var cue in group.Where(c => !c.Hand))
                {
                    if (cue.Fade)
                    {
                        if (tier <= Difficulty.Easy) continue;
                        if (cue.Length <= source.Resolution / 4 && tier != Difficulty.Expert) continue;
                        int stride = tier == Difficulty.Hard ? 2 : tier == Difficulty.Medium ? 2 : 1;
                        if (fadeIndex++ % stride != 0) continue;
                        if (time - fadeTime < MinimumFadeGap(tier)) continue;
                        fadeTime = time;
                    }
                    result.Add(cue);
                }
            }
            return result.OrderBy(c => c.Tick).ThenBy(c => c.Code).ToList();
        }

        private static List<(uint Start, uint End)> Union(IEnumerable<(uint Start, uint End)> intervals)
        {
            var result = new List<(uint Start, uint End)>();
            foreach (var p in intervals.Where(p => p.End > p.Start).OrderBy(p => p.Start).ThenBy(p => p.End))
            {
                if (result.Count == 0 || p.Start > result[^1].End) result.Add(p);
                else result[^1] = (result[^1].Start, Math.Max(result[^1].End, p.End));
            }
            return result;
        }

        private static string Safe(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Replace('[', '(').Replace(']', ')').Replace('{', '(').Replace('}', ')');
        private static string Serialize(SongChart source, DjSidecarInfo info, Dictionary<Difficulty, List<Cue>> tiers,
            List<(uint Start, uint End)> energy, List<(uint Start, uint End)> effects, uint? coda)
        {
            var b = new StringBuilder();
            void Line(string s) => b.Append(s).Append('\n');
            void Field(string key, string value) => Line("  " + key + " = \"" + Safe(value) + "\"");
            Line("[Song]"); Line("{"); Line("  Resolution = " + source.Resolution.ToString(CultureInfo.InvariantCulture)); Line("  Offset = 0");
            Field("DjGenerator", DjSidecar.GeneratorName); Field("DjGeneratorVersion", DjSidecar.GeneratorVersion); Field("DjSourceSha256", info.SourceHash);
            Line("  DjQuality = " + info.Quality.ToString(CultureInfo.InvariantCulture)); Line("  DjFlags = " + ((int)info.Flags).ToString(CultureInfo.InvariantCulture));
            Field("DjFlagNames", info.Flags.ToString()); Line("  DjHidden = " + (info.Hidden ? "true" : "false"));
            Field("DjBeatStems", info.BeatStems); Field("DjMelodicStems", info.MelodicStems); Field("DjBackingStems", info.BackingStems);
            Line("}"); Line("[SyncTrack]"); Line("{");
            var sync = source.SyncTrack.Tempos.Select(t => (t.Tick, Text: FormattableString.Invariant($"  {t.Tick} = B {Math.Round(t.BeatsPerMinute * 1000, MidpointRounding.AwayFromZero):0}")))
                .Concat(source.SyncTrack.TimeSignatures.Select(t => (t.Tick, Text: FormattableString.Invariant($"  {t.Tick} = TS {t.Numerator} {(int)Math.Round(Math.Log(t.Denominator, 2))}"))));
            foreach (var ev in sync.OrderBy(e => e.Tick).ThenBy(e => e.Text, StringComparer.Ordinal)) Line(ev.Text);
            Line("}"); Line("[Events]"); Line("{");
            foreach (var section in source.Sections.Where(_ => source.HasAuthoredSections)) Line(FormattableString.Invariant($"  {section.Tick} = E \"section {Safe(section.Name)}\""));
            Line("}");
            foreach (var difficulty in tiers.Keys.OrderBy(d => (int)d))
            {
                Line("[" + difficulty + "DJTurntable]"); Line("{");
                foreach (var cue in tiers[difficulty]) Line(FormattableString.Invariant($"  {cue.Tick} = N {cue.Code} {cue.Length}"));
                foreach (var p in energy) Phrase(p, 2);
                if (difficulty >= Difficulty.Hard)
                    foreach (var p in effects)
                    {
                        // Start effects after the fader's entry window. Its hold can then remain stationary.
                        uint start = Math.Min(p.End, p.Start + source.Resolution);
                        // Avoid asking the same hand to turn the dial and move the fader at once.
                        foreach (var fade in tiers[difficulty].Where(c => c.Fade && c.Tick < p.End && (ulong)c.Tick + source.Resolution > start))
                        {
                            uint stop = fade.Tick > source.Resolution / 2 ? fade.Tick - source.Resolution / 2 : 0;
                            if (stop > start) Phrase((start, stop), 35);
                            start = Math.Max(start, fade.Tick + source.Resolution);
                        }
                        if (start < p.End) Phrase((start, p.End), 35);
                    }
                Line("}");
                void Phrase((uint Start, uint End) p, int code)
                {
                    uint finish = Math.Min(p.End, coda ?? source.GetLastTick());
                    if (finish > p.Start && tiers[difficulty].Any(c => c.Tick >= p.Start && c.Tick < finish && c.Code < 68))
                        Line(FormattableString.Invariant($"  {p.Start} = S {code} {finish - p.Start}"));
                }
            }
            return b.ToString();
        }

        /// <summary>Independent checks on loaded generator output, suitable for batch reports and tests.</summary>
        public static IReadOnlyList<string> Validate(SongChart chart)
        {
            var errors = new List<string>();
            int previousCount = -1;
            foreach (var difficulty in new[] { Difficulty.Beginner, Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Expert })
            {
                if (!chart.Dj.TryGetDifficulty(difficulty, out var track)) continue;
                var notes = track.Notes.SelectMany(n => new[] { n }.Concat(n.ChildNotes)).ToArray();
                int count = notes.Count(n => !n.IsFreestyle);
                if (count < previousCount) errors.Add(difficulty + ": nonmonotonic density");
                previousCount = count;
                var fades = notes.Where(n => n.IsCrossfade).OrderBy(n => n.Time).ToArray();
                for (int i = 1; i < fades.Length; i++)
                    if (fades[i].Time - fades[i - 1].Time + 1e-8 < MinimumFadeGap(difficulty)) errors.Add(difficulty + ": fade rate");
                foreach (var effect in track.Phrases.Where(p => p.Type == PhraseType.DjEffectAll))
                    if (fades.Any(f => f.Tick >= effect.Tick && f.Tick < effect.TickEnd)) errors.Add(difficulty + ": simultaneous dial/fade change");
                double last = double.NegativeInfinity, scratch = double.NegativeInfinity, zoneEnd = double.NegativeInfinity;
                foreach (var g in notes.Where(n => n.IsTap || n.IsScratch || n.IsScratchZone).GroupBy(n => n.Tick))
                {
                    double time = chart.SyncTrack.TickToTime(g.Key);
                    if (time - last + 1e-8 < MinimumGap(difficulty) || time + 1e-8 < zoneEnd) errors.Add(difficulty + ": minimum gap/zone overlap");
                    if (g.Count() > MaximumChord(difficulty) || g.Select(n => n.Lane).Distinct().Count() != g.Count() ||
                        (g.Any(n => n.IsScratch || n.IsScratchZone) && g.Count() != 1)) errors.Add(difficulty + ": illegal lanes");
                    foreach (var n in g)
                    {
                        if (n.IsScratch)
                        {
                            if (MaximumScratchRate(difficulty) == 0 || time - scratch + 1e-8 < 1 / MaximumScratchRate(difficulty)) errors.Add(difficulty + ": scratch rate");
                            scratch = time;
                        }
                        if (n.IsScratchZone) zoneEnd = n.TimeEnd + MinimumGap(difficulty);
                    }
                    last = time;
                }
            }
            return errors;
        }
    }
}
