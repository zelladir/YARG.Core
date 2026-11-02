using System;

namespace YARG.Core.Chart
{
    /// <summary>Single-turntable note numbers from the DJ Hero Customs .chart proposal.</summary>
    public enum DjNoteType
    {
        GreenTap = 0,
        RedTap = 1,
        BlueTap = 2,
        OpenTap = 3,
        GreenScratchUp = 4,
        RedScratchUp = 5,
        BlueScratchUp = 6,
        OpenScratchUp = 7,
        GreenScratchDown = 8,
        RedScratchDown = 9,
        BlueScratchDown = 10,
        OpenScratchDown = 11,
        GreenScratchAny = 12,
        RedScratchAny = 13,
        BlueScratchAny = 14,
        OpenScratchAny = 15,
        GreenScratchZone = 16,
        RedScratchZone = 17,
        BlueScratchZone = 18,
        OpenScratchZone = 19,
        GreenFreestyleSample = 20,
        RedFreestyleSample = 21,
        BlueFreestyleSample = 22,
        GreenFreestyleScratch = 24,
        RedFreestyleScratch = 25,
        BlueFreestyleScratch = 26,
        CrossfadeGreen = 64,
        CrossfadeCenter = 65,
        CrossfadeBlue = 66,
        // 67 modifies a crossfade; it is not a playable note.
        FreestyleCrossfade = 68,
        FreestyleCrossfadeGreen = 69,
        FreestyleCrossfadeBlue = 70,
        EffectsLeft = 96,
        EffectsRight = 97,
    }

    public enum DjLane
    {
        None = -1,
        Green = 0,
        Red = 1,
        Blue = 2,
        Open = 3,
    }

    /// <summary>
    /// A tap, scratch, zone, crossfade, or effects-dial cue. Simultaneous events
    /// use the usual parent/child note representation, even across control types.
    /// </summary>
    public sealed class DjNote : Note<DjNote>
    {
        public DjNoteType Type { get; }
        public bool IsSpike { get; }
        public override int LaneNote => (int) Type;

        public bool IsTap => Type is >= DjNoteType.GreenTap and <= DjNoteType.OpenTap;
        public bool IsScratch => Type is >= DjNoteType.GreenScratchUp and <= DjNoteType.OpenScratchAny;
        public bool IsScratchZone => Type is >= DjNoteType.GreenScratchZone and <= DjNoteType.OpenScratchZone;
        public bool IsCrossfade => Type is >= DjNoteType.CrossfadeGreen and <= DjNoteType.CrossfadeBlue;
        public bool IsFreestyle => Type is >= DjNoteType.GreenFreestyleSample and <= DjNoteType.BlueFreestyleScratch
            or >= DjNoteType.FreestyleCrossfade and <= DjNoteType.FreestyleCrossfadeBlue;

        public DjLane Lane => (int) Type < 32 ? (DjLane) ((int) Type % 4) : DjLane.None;

        public DjNote(DjNoteType type, bool isSpike, NoteFlags flags,
            double time, double timeLength, uint tick, uint tickLength)
            : base(flags, time, timeLength, tick, tickLength)
        {
            if (!Enum.IsDefined(typeof(DjNoteType), type))
                throw new ArgumentOutOfRangeException(nameof(type));

            Type = type;
            IsSpike = IsCrossfade && isSpike;
        }

        private DjNote(DjNote other) : base(other)
        {
            Type = other.Type;
            IsSpike = other.IsSpike;
        }

        protected override DjNote CloneNote() => new(this);

        protected override void CopyFlags(DjNote other)
        {
            // DJ-specific properties are immutable; general flags are copied by Note.
        }
    }
}
