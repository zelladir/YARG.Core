using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YARG.Core.Chart
{
    public enum XmkGame { DjHero1, DjHero2 }

    public sealed class XmkImportOptions
    {
        public XmkGame Game { get; set; } = XmkGame.DjHero2;
        public Difficulty Difficulty { get; set; } = Difficulty.Expert;
        public uint Resolution { get; set; } = 1920;
        // DJH1 can require tempo supplied by external metadata. Never silently assume it.
        public double? FallbackBpm { get; set; }
        public bool RequireValidChecksum { get; set; }
    }

    public sealed class XmkImportResult
    {
        public SongChart Chart { get; internal set; } = null!;
        public uint Version { get; internal set; }
        public bool ChecksumValid { get; internal set; }
        public List<string> Diagnostics { get; } = new();
        public List<XmkEvent> UnsupportedEvents { get; } = new();
        public List<string> Authors { get; } = new();
    }

    /// <summary>Raw unsupported data retained in the conversion report for future mappings.</summary>
    public readonly struct XmkEvent
    {
        public readonly float Position, Length;
        public readonly uint Type, Extra;
        public XmkEvent(float position, uint type, float length, uint extra)
        { Position = position; Type = type; Length = length; Extra = extra; }
    }

    /// <summary>
    /// Independent FSGMUB reader. Format facts cross-checked against djhtools (MIT) and ANYDIR.
    /// No Dj-Engine implementation is used. See docs/dj-mode/XMK.md for provenance and limitations.
    /// </summary>
    public static class XmkImporter
    {
        private const uint BeatLength = 0x0B000001, Bpm = 0x0B000002;
        private const int MaxEntries = 250000, MaxBytes = 32 * 1024 * 1024;
        private const double MaxMeasures = 10000;

        public static XmkImportResult FromFile(string path, XmkImportOptions? options = null)
        {
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("XMK exceeds the input-size limit.");
            if (options == null)
            {
                options = new XmkImportOptions { Game = Path.GetExtension(path).Equals(".fsgmub", StringComparison.OrdinalIgnoreCase)
                    ? XmkGame.DjHero1 : XmkGame.DjHero2 };
                string name = Path.GetFileNameWithoutExtension(path);
                foreach (var difficulty in new[] { Difficulty.Beginner, Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Expert })
                    if (name.Equals("DJ_" + difficulty, StringComparison.OrdinalIgnoreCase)) options.Difficulty = difficulty;
            }
            return Import(File.ReadAllBytes(path), options);
        }

        public static XmkImportResult Import(ReadOnlySpan<byte> bytes, XmkImportOptions? options = null)
        {
            options ??= new XmkImportOptions();
            if (options.Resolution < 48 || options.Resolution > 9600 ||
                !Enum.IsDefined(typeof(XmkGame), options.Game) ||
                options.Difficulty is not (Difficulty.Beginner or Difficulty.Easy or Difficulty.Medium or Difficulty.Hard or Difficulty.Expert))
                throw new ArgumentOutOfRangeException(nameof(options));
            if (bytes.Length < 16 || bytes.Length > MaxBytes) throw new InvalidDataException("Invalid XMK header/size.");
            uint version = U32(bytes, 0), count = U32(bytes, 8), stringSize = U32(bytes, 12);
            if (version is not (1 or 2)) throw new InvalidDataException($"Unsupported FSGMUB version {version}.");
            long validLength = 16L + count * 16L + stringSize;
            if (count > MaxEntries || validLength > bytes.Length) throw new InvalidDataException("Truncated or oversized XMK event/string table.");
            var result = new XmkImportResult { Version = version, ChecksumValid = Crc32(bytes.Slice(8, (int) validLength - 8)) == U32(bytes, 4) };
            if (!result.ChecksumValid)
            {
                if (options.RequireValidChecksum) throw new InvalidDataException("XMK CRC32 mismatch.");
                result.Diagnostics.Add("CRC32 mismatch; imported with structural validation.");
            }
            if (bytes.Slice((int) validLength).IndexOfAnyExceptZero() >= 0)
                result.Diagnostics.Add("Nonzero trailing bytes are outside the declared event/string data.");
            var events = new List<XmkEvent>((int) count);
            for (int i = 0; i < count; i++)
            {
                int offset = 16 + i * 16;
                var entry = new XmkEvent(F32(bytes, offset), U32(bytes, offset + 4), F32(bytes, offset + 8), U32(bytes, offset + 12));
                if (!Finite(entry.Position) || !Finite(entry.Length) || entry.Position < 0 || entry.Length < 0 ||
                    (double) entry.Position + entry.Length > MaxMeasures)
                    throw new InvalidDataException($"Invalid timing at XMK event {i}.");
                events.Add(entry);
            }
            // Stable ordering, including duplicate tempo events and same-time control markers.
            events = events.OrderBy(e => e.Position).ToList();
            var chart = result.Chart = new SongChart(options.Resolution);
            uint ToTick(double measures) => checked((uint) Math.Round(measures * 4 * options.Resolution, MidpointRounding.AwayFromZero));
            var tempoEntries = events.Where(e => e.Type == BeatLength || e.Type == Bpm).GroupBy(e => ToTick(e.Position));
            foreach (var group in tempoEntries)
            {
                // Beat-length is the game's timing source. CHART_BPM is a nominal/unused annotation.
                var lengths = group.Where(e => e.Type == BeatLength).ToArray();
                double bpm;
                if (lengths.Length > 0)
                {
                    if (lengths.Any(e => e.Extra == 0)) throw new InvalidDataException("Zero beat duration.");
                    bpm = 60000000.0 / lengths[0].Extra;
                    if (lengths.Skip(1).Any(e => e.Extra != lengths[0].Extra))
                        throw new InvalidDataException("Conflicting beat durations at the same tick.");
                }
                else
                {
                    bpm = BitConverter.Int32BitsToSingle(unchecked((int) group.First().Extra));
                    result.Diagnostics.Add($"Using nominal CHART_BPM at tick {group.Key}; no beat-length event there.");
                }
                ValidateBpm(bpm);
                if (chart.SyncTrack.Tempos.Count == 0 && group.Key != 0) AddInitialTempo();
                double time = group.Key == 0 ? 0 : chart.SyncTrack.TickToTime(group.Key);
                chart.SyncTrack.Tempos.Add(new TempoChange(bpm, time, group.Key));
            }
            if (chart.SyncTrack.Tempos.Count == 0) AddInitialTempo();
            void AddInitialTempo()
            {
                if (!options.FallbackBpm.HasValue) throw new InvalidDataException("No tempo at zero; supply FallbackBpm from song metadata.");
                ValidateBpm(options.FallbackBpm.Value);
                chart.SyncTrack.Tempos.Add(new TempoChange(options.FallbackBpm.Value, 0, 0));
                result.Diagnostics.Add("Initial tempo supplied by caller metadata.");
            }
            chart.SyncTrack.TimeSignatures.Add(new TimeSignatureChange(4, 4, 0, 0, 0, 0, 0, 0));
            var track = new InstrumentDifficulty<DjNote>(Instrument.DjSingle, options.Difficulty);
            var notes = new List<DjNote>();
            var seen = new HashSet<(uint, DjNoteType)>();
            foreach (var entry in events)
            {
                uint tick = ToTick(entry.Position), end = ToTick((double) entry.Position + entry.Length);
                double time = chart.SyncTrack.TickToTime(tick), duration = chart.SyncTrack.TickToTime(end) - time;
                uint type = entry.Type;
                if (options.Game == XmkGame.DjHero1 && type is >= 48 and <= 51) type -= 28;
                DjNoteType? mapped = type switch
                {
                    0 => DjNoteType.GreenTap, 1 => DjNoteType.BlueTap, 2 => DjNoteType.RedTap,
                    3 => DjNoteType.GreenScratchUp, 4 => DjNoteType.BlueScratchUp,
                    5 => DjNoteType.GreenScratchDown, 6 => DjNoteType.BlueScratchDown,
                    7 => DjNoteType.GreenScratchAny, 8 => DjNoteType.BlueScratchAny,
                    9 or 28 => DjNoteType.CrossfadeBlue, 10 or 29 => DjNoteType.CrossfadeCenter,
                    11 or 27 => DjNoteType.CrossfadeGreen, 16 => DjNoteType.RedFreestyleSample,
                    17 => DjNoteType.FreestyleCrossfade, 20 => DjNoteType.GreenScratchZone,
                    21 => DjNoteType.BlueScratchZone, 31 => DjNoteType.FreestyleCrossfadeGreen,
                    32 => DjNoteType.FreestyleCrossfadeBlue, _ => null
                };
                if (mapped.HasValue)
                {
                    bool spike = type is >= 27 and <= 29 || options.Game == XmkGame.DjHero1 && type is >= 9 and <= 11 && end - tick <= options.Resolution / 4;
                    if (seen.Add((tick, mapped.Value)))
                        notes.Add(new DjNote(mapped.Value, spike, NoteFlags.None, time, duration, tick, end - tick));
                    else result.Diagnostics.Add($"Duplicate {mapped.Value} at tick {tick}; first retained.");
                    if (type == 16) track.TextEvents.Add(new TextEvent($"dj_sample {entry.Extra}", time, tick));
                    continue;
                }
                PhraseType? phrase = type switch
                {
                    12 => PhraseType.DjEffectGreen, 13 => PhraseType.DjEffectBlue, 14 => PhraseType.DjEffectRed,
                    15 => PhraseType.StarPower, 22 => PhraseType.DjEffectAll, 23 => PhraseType.DjCrossfadeNull, _ => null
                };
                if (phrase.HasValue)
                {
                    track.Phrases.Add(new Phrase(phrase.Value, time, duration, tick, end - tick));
                    continue;
                }
                if (type == 0x09FFFFFF || type == 0x0AFFFFFF || type == 0x0B000000)
                {
                    string text = ReadString(bytes, entry.Extra, count, stringSize);
                    if (type == 0x09FFFFFF) chart.Sections.Add(new Section(text, time, tick));
                    else if (type == 0x0AFFFFFF) result.Authors.Add(text);
                    else track.TextEvents.Add(new TextEvent(text, time, tick));
                }
                else if (type is >= 0x05FFFFFF and <= 0x06000009)
                {
                    string[] names = { "filter", "beatroll", "bitreduce", "wahwah", "ringmod", "stutter", "flanger", "robot", "adv_beatroll", "filter", "delay" };
                    track.TextEvents.Add(new TextEvent("dj_effect " + names[type - 0x05FFFFFF], time, tick));
                }
                else if (type != BeatLength && type != Bpm && type != uint.MaxValue)
                {
                    result.UnsupportedEvents.Add(entry);
                    result.Diagnostics.Add($"Unsupported XMK event 0x{entry.Type:X8} at measure {entry.Position}; retained in report.");
                }
            }
            foreach (var phrase in track.Phrases.Where(p => p.Type == PhraseType.StarPower))
            {
                var members = notes.Where(n => !n.IsFreestyle && n.Tick >= phrase.Tick && n.Tick < phrase.TickEnd).ToArray();
                foreach (var note in members)
                {
                    note.ActivateFlag(NoteFlags.StarPower);
                    if (note.Tick == members[0].Tick) note.ActivateFlag(NoteFlags.StarPowerStart);
                    if (note.Tick == members[members.Length - 1].Tick) note.ActivateFlag(NoteFlags.StarPowerEnd);
                }
            }
            foreach (var group in notes.GroupBy(n => n.Tick))
            {
                var parent = group.First();
                foreach (var child in group.Skip(1)) parent.AddChildNote(child);
                track.Notes.Add(parent);
            }
            for (int i = 0; i < track.Notes.Count; i++)
                foreach (var note in track.Notes[i].AllNotes)
                {
                    note.PreviousNote = i > 0 ? track.Notes[i - 1] : null;
                    note.NextNote = i + 1 < track.Notes.Count ? track.Notes[i + 1] : null;
                }
            chart.Dj.AddDifficulty(options.Difficulty, track);
            uint lastTick = events.Select(e => ToTick((double) e.Position + e.Length)).DefaultIfEmpty(0U).Max();
            chart.SyncTrack.FinishLoading(lastTick);
            return result;
        }

        private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        private static float F32(ReadOnlySpan<byte> data, int offset) => BitConverter.Int32BitsToSingle(unchecked((int) U32(data, offset)));
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void ValidateBpm(double bpm)
        {
            if (!Finite(bpm) || bpm < 1 || bpm > 1000) throw new InvalidDataException("Invalid/unsupported XMK tempo (1..1000 BPM).");
        }
        private static string ReadString(ReadOnlySpan<byte> data, uint pointer, uint count, uint size)
        {
            long relative = (long) pointer - count * 16L;
            if (relative < 0 || relative >= size) throw new InvalidDataException("XMK string pointer is outside the string blob.");
            var remaining = data.Slice(checked((int) (16L + pointer)), (int) (size - relative));
            int length = remaining.IndexOf((byte) 0);
            if (length < 0) throw new InvalidDataException("Unterminated XMK string.");
            try { return new UTF8Encoding(false, true).GetString(remaining.Slice(0, length)); }
            catch (DecoderFallbackException exception) { throw new InvalidDataException("Invalid UTF-8 XMK string.", exception); }
        }
        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = uint.MaxValue;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0);
            }
            return ~crc;
        }
        private static int IndexOfAnyExceptZero(this ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++) if (data[i] != 0) return i;
            return -1;
        }
    }
}
