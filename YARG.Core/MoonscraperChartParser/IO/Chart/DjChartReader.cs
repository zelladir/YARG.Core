using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YARG.Core;
using YARG.Core.Chart;
using YARG.Core.Extensions;
using YARG.Core.Logging;
using YARG.Core.Utility;

namespace MoonscraperChartEditor.Song.IO
{
    using AsciiTrimSplitter = SpanSplitter<char, AsciiTrimSplitProcessor>;

    /// <summary>Reads the single-turntable DJ Hero Customs proposal using the normal .chart section reader.</summary>
    internal static class DjChartReader
    {
        private const string TRACK_NAME = "DJTurntable";

        public static bool TryRead(MoonSong song, ReadOnlySpan<char> sectionName, AsciiTrimSplitter lines)
        {
            if (!sectionName.EndsWith(TRACK_NAME, StringComparison.Ordinal))
                return false;

            var difficultyName = sectionName[..^TRACK_NAME.Length];
            Difficulty? difficulty = difficultyName.ToString() switch
            {
                "Beginner" => Difficulty.Beginner,
                "Easy" => Difficulty.Easy,
                "Medium" => Difficulty.Medium,
                "Hard" => Difficulty.Hard,
                "Expert" => Difficulty.Expert,
                _ => null,
            };
            if (!difficulty.HasValue)
                return true;

            if (song.DjCharts.ContainsKey(difficulty.Value))
            {
                YargLogger.LogWarning($"Ignoring duplicate DJ section [{sectionName.ToString()}].");
                return true;
            }

            var chart = new InstrumentDifficulty<DjNote>(Instrument.DjSingle, difficulty.Value);
            var swaps = new HashSet<uint>();
            var seenNotes = new HashSet<(uint, DjNoteType)>();
            var sorted = new List<(uint Tick, int Order, string Data)>();
            int order = 0;
            foreach (var line in lines)
            {
                if (line.IsEmpty)
                    continue;

                var tickText = line.SplitOnceTrimmed('=', out var data);
                if (!TryReadUInt(tickText, out uint tick))
                {
                    YargLogger.LogWarning($"Ignoring DJ line with invalid tick: {line.ToString()}");
                    continue;
                }
                sorted.Add((tick, order++, data.ToString()));
            }
            sorted.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

            // Collect modifiers first so their order relative to notes is irrelevant.
            var rawNotes = new List<(uint Tick, uint Length, DjNoteType Type)>();
            foreach (var (tick, _, data) in sorted)
            {
                try
                {
                    var code = data.SplitOnceTrimmed(' ', out var remaining);
                    double time = song.TickToTime(tick);
                    if (code.SequenceEqual("E"))
                    {
                        chart.TextEvents.Add(new TextEvent(remaining.Trim().Trim('"').ToString(), time, tick));
                        continue;
                    }

                    if (!code.SequenceEqual("N") && !code.SequenceEqual("S"))
                        throw new InvalidDataException("Unknown DJ event code.");

                    var typeText = remaining.SplitOnceTrimmed(' ', out var lengthText);
                    if (!TryReadUInt(typeText, out uint type) || !TryReadUInt(lengthText, out uint length)
                        || length > uint.MaxValue - tick)
                        throw new InvalidDataException("Invalid DJ event number or length.");

                    if (code.SequenceEqual("N"))
                    {
                        if (type == 67)
                        {
                            swaps.Add(tick);
                        }
                        else if (type <= int.MaxValue && Enum.IsDefined(typeof(DjNoteType), (int) type))
                        {
                            var noteType = (DjNoteType) type;
                            if (seenNotes.Add((tick, noteType)))
                                rawNotes.Add((tick, length, noteType));
                        }
                        else
                        {
                            YargLogger.LogWarning($"Ignoring unsupported single-table DJ note {type} at {tick}.");
                        }
                    }
                    else
                    {
                        PhraseType? phraseType = type switch
                        {
                            2 => PhraseType.StarPower,
                            32 => PhraseType.DjEffectGreen,
                            33 => PhraseType.DjEffectRed,
                            34 => PhraseType.DjEffectBlue,
                            35 => PhraseType.DjEffectAll,
                            96 => PhraseType.DjCrossfadeNull,
                            _ => null,
                        };
                        if (phraseType.HasValue)
                        {
                            chart.Phrases.Add(new Phrase(phraseType.Value, time,
                                song.TickToTime(tick + length) - time, tick, length));
                        }
                        else
                        {
                            YargLogger.LogWarning($"Ignoring unsupported single-table DJ phrase {type} at {tick}.");
                        }
                    }
                }
                catch (InvalidDataException exception)
                {
                    YargLogger.LogException(exception, $"Ignoring DJ event at {tick}: {data}");
                }
            }

            foreach (var (tick, length, type) in rawNotes)
            {
                double time = song.TickToTime(tick);
                // Resolution is quarter-note ticks; one sixteenth is resolution / 4.
                bool spike = (length <= song.resolution / 4) ^ swaps.Contains(tick);
                chart.Notes.Add(new DjNote(type, spike, NoteFlags.None, time,
                    song.TickToTime(tick + length) - time, tick, length));
            }

            foreach (var phrase in chart.Phrases)
            {
                if (phrase.Type != PhraseType.StarPower)
                    continue;

                DjNote? first = null;
                DjNote? last = null;
                foreach (var note in chart.Notes)
                {
                    if (note.Tick < phrase.Tick || note.Tick >= phrase.TickEnd || note.IsFreestyle)
                        continue;
                    note.ActivateFlag(NoteFlags.StarPower);
                    first ??= note;
                    last = note;
                }

                if (first == null || last == null)
                    continue;

                // Mark every component on the first/last tick, including chords.
                foreach (var note in chart.Notes)
                {
                    if (note.IsFreestyle)
                        continue;
                    if (note.Tick == first.Tick)
                        note.ActivateFlag(NoteFlags.StarPowerStart);
                    if (note.Tick == last.Tick)
                        note.ActivateFlag(NoteFlags.StarPowerEnd);
                }
            }

            song.DjCharts.Add(difficulty.Value, chart);
            return true;
        }

        private static bool TryReadUInt(ReadOnlySpan<char> text, out uint value)
            => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
