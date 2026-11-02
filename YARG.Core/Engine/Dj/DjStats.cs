using System;
using System.IO;
using YARG.Core.Replays;

namespace YARG.Core.Engine.Dj
{
    public sealed class DjStats : BaseStats
    {
        public int Misses, TapsHit, ScratchesHit, CrossfadesHit, SpikesHit, EffectsCuesHit;
        public int CoverageUnitsHit, CoverageUnitsTotal, EffectsUnitsHit, EffectsUnitsTotal, EffectsScore;
        public override bool IsFullCombo => NotesHit == TotalNotes && Misses == 0 && CoverageUnitsHit == CoverageUnitsTotal;

        public DjStats() { }
        public DjStats(DjStats other) : base(other)
        {
            Misses = other.Misses; TapsHit = other.TapsHit; ScratchesHit = other.ScratchesHit;
            CrossfadesHit = other.CrossfadesHit; SpikesHit = other.SpikesHit; EffectsCuesHit = other.EffectsCuesHit;
            CoverageUnitsHit = other.CoverageUnitsHit; CoverageUnitsTotal = other.CoverageUnitsTotal;
            EffectsUnitsHit = other.EffectsUnitsHit; EffectsUnitsTotal = other.EffectsUnitsTotal; EffectsScore = other.EffectsScore;
        }
        public override void Reset()
        {
            base.Reset();
            Misses = TapsHit = ScratchesHit = CrossfadesHit = SpikesHit = EffectsCuesHit = 0;
            CoverageUnitsHit = EffectsUnitsHit = EffectsScore = 0;
        }
        public override void Serialize(BinaryWriter writer) => throw new NotSupportedException("DJ replay stats are not yet supported.");
        public override ReplayStats ConstructReplayStats(string name, bool isReplayPlayer) =>
            throw new NotSupportedException("DJ replay stats are not yet supported.");
    }
}
