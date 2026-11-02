using System.Collections.Generic;

namespace YARG.Core.Chart
{
    internal partial class MoonSongLoader
    {
        public InstrumentTrack<DjNote> LoadDjTrack()
        {
            var track = new InstrumentTrack<DjNote>(Instrument.DjSingle);
            foreach (var (difficulty, source) in _moonSong.DjCharts)
            {
                var notes = new List<DjNote>(source.Notes.Count);
                foreach (var note in source.Notes)
                {
                    // A one-tick crossfade/spike is meaningful; guitar chord snapping
                    // and sustain cutoffs must not change DJ event timing.
                    AddNoteToList(notes, note.Clone(), allowSnap: false);
                }

                track.AddDifficulty(difficulty, new InstrumentDifficulty<DjNote>(
                    Instrument.DjSingle, difficulty, notes, source.Phrases, source.TextEvents));
            }
            return track;
        }
    }
}
