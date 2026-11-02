using System;
using System.IO;

namespace YARG.Core.Engine.Dj
{
    /// <summary>Experimental unranked policy 1. Values require turntable measurements and playtesting.</summary>
    public sealed class DjEngineParameters : BaseEngineParameters
    {
        public const int PolicyVersion = 1;
        public readonly double ScratchActivate, ScratchRelease, FadeEnter, FadeRelease;
        public readonly double DropoutGrace, CoverageRequired, EffectsTravelPerBeat;
        public readonly bool AutoFadeEuphoria, GateOuterLanes;
        public readonly int NotePoints, HoldPointsPerBeat, EffectsPointsPerBeat, Subdivisions;

        public DjEngineParameters(HitWindowSettings hitWindow, int maxMultiplier = 4,
            double scratchActivate = 6, double scratchRelease = 3, double fadeEnter = .55, double fadeRelease = .35,
            double dropoutGrace = .05, double coverageRequired = .8, double effectsTravelPerBeat = .125,
            bool autoFadeEuphoria = true, bool gateOuterLanes = false, int notePoints = 50,
            int holdPointsPerBeat = 25, int effectsPointsPerBeat = 25, int subdivisions = 4)
            : base(hitWindow, maxMultiplier, 0, dropoutGrace,
                new[] { .10f, .20f, .35f, .55f, .75f }, new float[5], false)
        {
            if (hitWindow.IsDynamic || !Positive(hitWindow.MaxWindow) || hitWindow.MaxWindow > 2 ||
                !Positive(hitWindow.Scale) || hitWindow.FrontToBackRatio < 0 || hitWindow.FrontToBackRatio > 2 ||
                !Finite(hitWindow.FrontToBackRatio)) throw new ArgumentException("DJ requires a finite fixed hit window.");
            if (!Positive(scratchActivate) || scratchActivate > 128 || !Finite(scratchRelease) ||
                scratchRelease < 0 || scratchRelease >= scratchActivate || !Positive(fadeEnter) || fadeEnter > 1 ||
                !Finite(fadeRelease) || fadeRelease < 0 || fadeRelease >= fadeEnter ||
                !Finite(dropoutGrace) || dropoutGrace < 0 || dropoutGrace > 1 ||
                !Positive(coverageRequired) || coverageRequired > 1 || !Positive(effectsTravelPerBeat) ||
                maxMultiplier < 1 || maxMultiplier > 8 || subdivisions < 1 || subdivisions > 64 ||
                notePoints < 0 || notePoints > 10000 || holdPointsPerBeat < 0 || holdPointsPerBeat > 10000 ||
                effectsPointsPerBeat < 0 || effectsPointsPerBeat > 10000)
                throw new ArgumentOutOfRangeException(nameof(scratchActivate), "Invalid DJ policy parameters.");
            ScratchActivate = scratchActivate; ScratchRelease = scratchRelease;
            FadeEnter = fadeEnter; FadeRelease = fadeRelease;
            DropoutGrace = dropoutGrace; CoverageRequired = coverageRequired; EffectsTravelPerBeat = effectsTravelPerBeat;
            AutoFadeEuphoria = autoFadeEuphoria; GateOuterLanes = gateOuterLanes;
            NotePoints = notePoints; HoldPointsPerBeat = holdPointsPerBeat;
            EffectsPointsPerBeat = effectsPointsPerBeat; Subdivisions = subdivisions;
            SongSpeed = 1;
        }

        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        private static bool Positive(double x) => Finite(x) && x > 0;

        public override void Serialize(BinaryWriter writer) =>
            throw new NotSupportedException("DJ policy serialization requires a future DJ replay format.");
    }
}
