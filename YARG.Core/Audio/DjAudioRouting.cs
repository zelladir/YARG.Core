using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YARG.Core.Audio
{
    public enum DjAudioMode { UnclaimedStems, WholeMixColor, GameplayOnly }
    public enum DjStemGroup { Excluded, Beat, Melodic, Backing }

    /// <summary>Pure routing/gain policy. Unity owns DSP, gain smoothing and the human-player claim snapshot.</summary>
    public static class DjAudioRouting
    {
        public static DjStemGroup Classify(SongStem stem) => stem switch
        {
            SongStem.Drums1 or SongStem.Drums2 or SongStem.Drums3 or SongStem.Drums4 or
                SongStem.Bass or SongStem.Rhythm => DjStemGroup.Beat,
            SongStem.Guitar or SongStem.Keys or SongStem.Vocals => DjStemGroup.Melodic,
            SongStem.Song => DjStemGroup.Backing,
            _ => DjStemGroup.Excluded
        };

        public static DjStemGroup ClassifyFile(string filename)
        {
            string extension = Path.GetExtension(filename).ToLowerInvariant();
            if (extension != ".ogg" && extension != ".opus" && extension != ".mp3" && extension != ".wav" && extension != ".aiff")
                return DjStemGroup.Excluded;
            string stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
            if (stem.EndsWith("_clean")) stem = stem.Substring(0, stem.Length - 6);
            if (stem.EndsWith("_explicit")) stem = stem.Substring(0, stem.Length - 9);
            return stem switch
            {
                "drums" or "drums_1" or "drums_2" or "drums_3" or "drums_4" or "bass" or "rhythm" => DjStemGroup.Beat,
                "guitar" or "keys" or "vocals" or "vocals_1" or "vocals_2" => DjStemGroup.Melodic,
                "song" or "backing" => DjStemGroup.Backing,
                _ => DjStemGroup.Excluded
            };
        }

        public static IReadOnlyCollection<SongStem> HumanClaims(IEnumerable<Instrument> instruments)
        {
            var result = new HashSet<SongStem>();
            foreach (var instrument in instruments)
            {
                switch (instrument)
                {
                    case Instrument.FourLaneDrums: case Instrument.ProDrums: case Instrument.FiveLaneDrums: case Instrument.EliteDrums:
                        result.UnionWith(new[] { SongStem.Drums1, SongStem.Drums2, SongStem.Drums3, SongStem.Drums4 }); break;
                    case Instrument.FiveFretBass: case Instrument.SixFretBass: case Instrument.ProBass_17Fret: case Instrument.ProBass_22Fret:
                        result.Add(SongStem.Bass); break;
                    case Instrument.FiveFretRhythm: case Instrument.SixFretRhythm: case Instrument.FiveFretCoopGuitar: case Instrument.SixFretCoopGuitar:
                        result.Add(SongStem.Rhythm); break;
                    case Instrument.Keys: case Instrument.ProKeys: result.Add(SongStem.Keys); break;
                    case Instrument.Vocals: case Instrument.Harmony: case Instrument.PartyVocals: result.Add(SongStem.Vocals); break;
                    case Instrument.FiveFretGuitar: case Instrument.SixFretGuitar: case Instrument.ProGuitar_17Fret: case Instrument.ProGuitar_22Fret:
                        result.Add(SongStem.Guitar); break;
                }
            }
            return result;
        }

        public static bool IsControlled(SongStem stem, DjAudioMode mode, IEnumerable<SongStem> humanClaims, bool allowVocals = false)
        {
            if (mode == DjAudioMode.GameplayOnly || Classify(stem) == DjStemGroup.Excluded ||
                (mode == DjAudioMode.UnclaimedStems && stem == SongStem.Vocals && !allowVocals)) return false;
            return mode == DjAudioMode.WholeMixColor || !humanClaims.Contains(stem);
        }

        /// <summary>Multiplicative DJ gain only. fader -1 = beat, +1 = melodic; center leaves original balance intact.</summary>
        public static double Gain(SongStem stem, DjAudioMode mode, IEnumerable<SongStem> humanClaims,
            double fader, bool missed = false, bool allowVocals = false, double colorDepthDb = 6)
        {
            if (double.IsNaN(fader) || double.IsInfinity(fader) || double.IsNaN(colorDepthDb) ||
                double.IsInfinity(colorDepthDb) || colorDepthDb < 0 || colorDepthDb > 24)
                throw new ArgumentOutOfRangeException(nameof(fader));
            if (!IsControlled(stem, mode, humanClaims, allowVocals)) return 1;
            fader = Math.Max(-1, Math.Min(1, fader));
            var group = Classify(stem);
            if (mode == DjAudioMode.WholeMixColor)
                return Math.Pow(10, (group == DjStemGroup.Beat ? -fader : group == DjStemGroup.Melodic ? fader : 0) * colorDepthDb / 20);
            if (missed) return 0;
            return group == DjStemGroup.Beat ? 1 - Math.Max(0, fader) :
                group == DjStemGroup.Melodic ? 1 + Math.Min(0, fader) : 1;
        }
    }
}
