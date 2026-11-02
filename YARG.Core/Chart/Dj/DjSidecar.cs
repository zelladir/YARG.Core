using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace YARG.Core.Chart
{
    [Flags]
    public enum DjQualityFlags
    {
        None = 0, MissingSections = 1, SparseSource = 2, IrregularTempo = 4, RubatoTempo = 8,
        ExtremeDensity = 16, SyncMismatch = 32, ConstraintViolation = 64, SourceChanged = 128,
        InvalidSidecar = 256, NoPlayableNotes = 512
    }

    public sealed class DjSidecarInfo
    {
        public bool Generated { get; internal set; }
        public string Version { get; internal set; } = "";
        public string SourceHash { get; internal set; } = "";
        public int Quality { get; internal set; } = -1;
        public bool Hidden { get; internal set; }
        public DjQualityFlags Flags { get; internal set; }
        public string BeatStems { get; internal set; } = "";
        public string MelodicStems { get; internal set; } = "";
        public string BackingStems { get; internal set; } = "";
        public bool Accepted { get; internal set; }
        public DifficultyMask Difficulties { get; internal set; }
    }

    public static class DjSidecar
    {
        public const string GeneratorName = "YARG.djgen";
        public const string GeneratorVersion = "1.0.0";
        public const int HiddenThreshold = 60;
        public const int MaxSidecarBytes = 16 * 1024 * 1024;

        // Only fields in [Song] count. A mention in an event/comment cannot confer ownership.
        internal static Dictionary<string, string> Header(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            bool inSong = false, body = false;
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (!inSong) { if (line == "[Song]") inSong = true; continue; }
                if (line == "{") { body = true; continue; }
                if (line == "}") break;
                if (!body || line.StartsWith("//")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate Song property: " + key);
                result.Add(key, line.Substring(equals + 1).Trim().Trim('"'));
            }
            return result;
        }

        public static bool IsGeneratorOwned(string text)
        {
            try { return Header(text).TryGetValue("DjGenerator", out string? value) && value == GeneratorName; }
            catch (InvalidDataException) { return false; }
        }

        public static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static string ReadText(string path)
        {
            if (new FileInfo(path).Length > MaxSidecarBytes) throw new InvalidDataException("DJ sidecar exceeds 16 MiB.");
            return File.ReadAllText(path);
        }

        /// <summary>Rejects conflicting maps; accepted DJ events are retimed to the MIDI's exact map.</summary>
        public static DjSidecarInfo Merge(SongChart source, string text, string? sourceHash = null)
        {
            var info = new DjSidecarInfo();
            source.DjSidecar = info;
            source.Dj = new InstrumentTrack<DjNote>(Instrument.DjSingle);
            try
            {
                var header = Header(text);
                string Get(string key, string fallback = "") => header.TryGetValue(key, out var value) ? value : fallback;
                info.Generated = Get("DjGenerator") == GeneratorName;
                info.Version = Get("DjGeneratorVersion");
                info.SourceHash = Get("DjSourceSha256");
                info.BeatStems = Get("DjBeatStems"); info.MelodicStems = Get("DjMelodicStems"); info.BackingStems = Get("DjBackingStems");
                if (info.Generated)
                {
                    info.Quality = int.Parse(Get("DjQuality"), CultureInfo.InvariantCulture);
                    info.Flags = (DjQualityFlags) int.Parse(Get("DjFlags"), CultureInfo.InvariantCulture);
                    info.Hidden = bool.Parse(Get("DjHidden")) || info.Quality < HiddenThreshold;
                    if (info.Quality < 0 || info.Quality > 100 || info.SourceHash.Length != 64 ||
                        info.SourceHash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid generator metadata.");
                    if (sourceHash != null && !info.SourceHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                        return Reject(info, DjQualityFlags.SourceChanged);
                }
                // MIDI chart time starts at zero. song.ini delay is applied once, outside both charts.
                if (!double.TryParse(Get("Offset", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) || offset != 0)
                    return Reject(info, DjQualityFlags.SyncMismatch);
                if (!uint.TryParse(Get("Resolution"), NumberStyles.None, CultureInfo.InvariantCulture, out uint resolution) || resolution != source.Resolution ||
                    !ValidSyncSyntax(text)) return Reject(info, DjQualityFlags.SyncMismatch);
                if (!WithinSongBounds(source, text)) return Reject(info, DjQualityFlags.InvalidSidecar);
                var sidecar = SongChart.FromDotChart(ParseSettings.Default_Chart, text);
                if (!SyncMatches(source.SyncTrack, sidecar.SyncTrack)) return Reject(info, DjQualityFlags.SyncMismatch);
                foreach (Difficulty difficulty in new[] { Difficulty.Beginner, Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Expert })
                {
                    if (!sidecar.Dj.TryGetDifficulty(difficulty, out var track)) continue;
                    foreach (var note in track.Notes.SelectMany(n => new[] { n }.Concat(n.ChildNotes))) Retime(note);
                    foreach (var phrase in track.Phrases) Retime(phrase);
                    foreach (var ev in track.TextEvents) Retime(ev);
                    if (track.Notes.Any(n => !n.IsFreestyle || n.ChildNotes.Any(c => !c.IsFreestyle)))
                        info.Difficulties |= (DifficultyMask) (1 << (int) difficulty);
                }
                if (info.Difficulties == DifficultyMask.None) return Reject(info, DjQualityFlags.NoPlayableNotes);
                source.Dj = sidecar.Dj;
                source.SyncTrack.GenerateBeatlines(source.GetLastTick());
                info.Accepted = true;
                return info;

                void Retime(ChartEvent ev)
                {
                    ev.Time = source.SyncTrack.TickToTime(ev.Tick);
                    ev.TimeLength = source.SyncTrack.TickToTime(ev.TickEnd) - ev.Time;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is FormatException || ex is OverflowException ||
                ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is KeyNotFoundException)
            {
                return Reject(info, DjQualityFlags.InvalidSidecar);
            }
        }

        internal static DjSidecarInfo Reject(DjSidecarInfo info, DjQualityFlags flag)
        {
            info.Flags |= flag; info.Hidden = true; info.Accepted = false; info.Difficulties = DifficultyMask.None;
            return info;
        }

        public static bool SyncMatches(SyncTrack midi, SyncTrack sidecar)
        {
            if (midi.Resolution != sidecar.Resolution || midi.Tempos.Count != sidecar.Tempos.Count ||
                midi.TimeSignatures.Count != sidecar.TimeSignatures.Count) return false;
            for (int i = 0; i < midi.Tempos.Count; i++)
                if (midi.Tempos[i].Tick != sidecar.Tempos[i].Tick ||
                    Math.Abs(midi.Tempos[i].BeatsPerMinute - sidecar.Tempos[i].BeatsPerMinute) > .000501) return false;
            for (int i = 0; i < midi.TimeSignatures.Count; i++)
                if (midi.TimeSignatures[i].Tick != sidecar.TimeSignatures[i].Tick ||
                    midi.TimeSignatures[i].Numerator != sidecar.TimeSignatures[i].Numerator ||
                    midi.TimeSignatures[i].Denominator != sidecar.TimeSignatures[i].Denominator) return false;
            return true;
        }

        private static bool ValidSyncSyntax(string text)
        {
            bool found = false, body = false, tempo = false;
            uint previous = 0;
            using var reader = new StringReader(text);
            string? line;
            var seen = new HashSet<(uint, string)>();
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (!found) { if (line == "[SyncTrack]") found = true; continue; }
                if (line == "{") { body = true; continue; }
                if (line == "}") return tempo;
                if (!body || line.Length == 0 || line.StartsWith("//")) continue;
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4 || fields[1] != "=" || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint tick) ||
                    tick < previous || !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value == 0 || !seen.Add((tick, fields[2]))) return false;
                previous = tick;
                if (fields[2] == "B")
                {
                    if (fields.Length != 4 || value > 10000000) return false;
                    tempo = true;
                }
                else if (fields[2] == "TS")
                {
                    if (value > 255 || fields.Length > 5 || (fields.Length == 5 &&
                        (!uint.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint power) || power > 7))) return false;
                }
                else return false; // Anchors are unsupported; do not silently discard them.
            }
            return false;
        }

        private static bool WithinSongBounds(SongChart source, string text)
        {
            // Bound optional sidecar events before the generic reader expands beatlines.
            // Allow sixteen extra 4/4 measures for hand-authored tails, but not unbounded corrupt ticks.
            ulong maximum = (ulong)source.GetLastTick() + (ulong)source.Resolution * 64;
            bool djTrack = false;
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("[")) { djTrack = line.EndsWith("DJTurntable]", StringComparison.Ordinal); continue; }
                if (!djTrack || line.Length == 0 || !char.IsDigit(line[0])) continue;
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (!ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong tick) || tick > maximum) return false;
                if (fields.Length >= 5 && (fields[2] == "N" || fields[2] == "S") &&
                    (!ulong.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out ulong length) || length > maximum - tick)) return false;
            }
            return true;
        }

        public static DjSidecarInfo? MergeFile(SongChart source, string midiPath)
        {
            string path = Path.Combine(Path.GetDirectoryName(midiPath)!, "dj.chart");
            if (!File.Exists(path)) return null;
            try { return Merge(source, ReadText(path), HashFile(midiPath)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return source.DjSidecar = Reject(new DjSidecarInfo(), DjQualityFlags.InvalidSidecar);
            }
        }

        /// <summary>Returns false for hand-authored/foreign/malformed headers. No writes in dry run.</summary>
        public static bool WriteGenerated(string path, string text)
        {
            if (!IsGeneratorOwned(text)) throw new ArgumentException("Output has no generator marker.", nameof(text));
            if (File.Exists(path) && !IsGeneratorOwned(ReadText(path))) return false;
            // Same-directory temporary file, followed by an atomic rename. Recheck ownership before replacement.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    if (!IsGeneratorOwned(ReadText(path))) return false;
                    File.Replace(temporary, path, null);
                }
                else File.Move(temporary, path); // fails safely if a new file appeared
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
