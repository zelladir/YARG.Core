using YARG.Core.Engine.Dj;
using YARG.Core.Input;

namespace YARG.Core.Game
{
    public partial class EnginePreset
    {
        public sealed class DjPreset
        {
            // Factories avoid sharing mutable preset objects between profiles.
            public static DjPreset Band() => new() { AutoFadeEuphoria = false, GateOuterLanes = false };
            // Retains Phase 1b values; DJH2-inspired, not a verified original-game score policy.
            public static DjPreset StandaloneDjHero2() => new();
            public HitWindowPreset HitWindow = new();
            public DjDeck Deck = DjDeck.Left;
            public bool InvertPlatter, InvertCrossfader, GateOuterLanes;
            public bool AutoFadeEuphoria = true;
            public double ScratchActivate = 6, ScratchRelease = 3, FadeEnter = .55, FadeRelease = .35;
            public double DropoutGrace = .05, CoverageRequired = .8, EffectsTravelPerBeat = .125;
            public int MaxMultiplier = 4, NotePoints = 50, HoldPointsPerBeat = 25, EffectsPointsPerBeat = 25, Subdivisions = 4;

            public DjEngineParameters Create() => new(HitWindow.Create(), MaxMultiplier,
                ScratchActivate, ScratchRelease, FadeEnter, FadeRelease, DropoutGrace, CoverageRequired,
                EffectsTravelPerBeat, AutoFadeEuphoria, GateOuterLanes, NotePoints, HoldPointsPerBeat,
                EffectsPointsPerBeat, Subdivisions);
            public DjInputNormalizer CreateNormalizer() => new(Deck, InvertPlatter, InvertCrossfader);
            public DjPreset Copy()
            {
                var copy = (DjPreset) MemberwiseClone();
                copy.HitWindow = HitWindow.Copy();
                return copy;
            }
        }
    }
}
