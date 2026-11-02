using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine.Dj;
using YARG.Core.Game;

namespace YARG.Core.UnitTests.Chart;

public class XmkImporterTests
{
    private readonly record struct Entry(float Position, uint Type, float Length = 0, uint Extra = 0);
    private static readonly Entry Tempo = new(0, 0x0B000001, 0, 500000);

    // Entirely synthetic binary fixture generator. No copyrighted chart bytes are embedded.
    private static byte[] Bytes(Entry[] entries, byte[]? strings = null, uint version = 2, bool pad = true)
    {
        strings ??= Array.Empty<byte>();
        int length = 16 + entries.Length * 16 + strings.Length;
        var bytes = new byte[pad ? (length + 31) / 32 * 32 : length];
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        Write(0, version); Write(8, (uint) entries.Length); Write(12, (uint) strings.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            int at = 16 + i * 16; var e = entries[i];
            Write(at, BitConverter.SingleToUInt32Bits(e.Position)); Write(at + 4, e.Type);
            Write(at + 8, BitConverter.SingleToUInt32Bits(e.Length)); Write(at + 12, e.Extra);
        }
        strings.CopyTo(bytes, 16 + entries.Length * 16);
        // Table-driven IEEE CRC32, independent of the importer's bit-at-a-time implementation.
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xEDB88320U ^ (value >> 1);
            table[i] = value;
        }
        uint crc = uint.MaxValue;
        for (int i = 8; i < length; i++) crc = table[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
        Write(4, ~crc);
        return bytes;
    }
    private static List<DjNote> Notes(XmkImportResult result, Difficulty difficulty = Difficulty.Expert) =>
        result.Chart.Dj.GetDifficulty(difficulty).Notes.SelectMany(n => new[] { n }.Concat(n.ChildNotes)).ToList();

    [Test]
    public void MapsDjh2NotesPhrasesAndExplicitSpikesWithoutLengthHeuristics()
    {
        uint[] types = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 16, 17, 20, 21, 27, 28, 29, 31, 32 };
        var result = XmkImporter.Import(Bytes(new[] { Tempo }.Concat(types.Select((type, i) => new Entry(i + 1, type, .01f))).ToArray()));
        Assert.That(result.ChecksumValid, Is.True);
        Assert.That(result.Diagnostics, Is.Empty);
        Assert.That(Notes(result).Select(n => n.Type), Is.EqualTo(new[] { DjNoteType.GreenTap, DjNoteType.BlueTap, DjNoteType.RedTap,
            DjNoteType.GreenScratchUp, DjNoteType.BlueScratchUp, DjNoteType.GreenScratchDown, DjNoteType.BlueScratchDown,
            DjNoteType.GreenScratchAny, DjNoteType.BlueScratchAny, DjNoteType.CrossfadeBlue, DjNoteType.CrossfadeCenter,
            DjNoteType.CrossfadeGreen, DjNoteType.RedFreestyleSample, DjNoteType.FreestyleCrossfade,
            DjNoteType.GreenScratchZone, DjNoteType.BlueScratchZone, DjNoteType.CrossfadeGreen, DjNoteType.CrossfadeBlue,
            DjNoteType.CrossfadeCenter, DjNoteType.FreestyleCrossfadeGreen, DjNoteType.FreestyleCrossfadeBlue }));
        Assert.That(Notes(result).Count(n => n.IsSpike), Is.EqualTo(3));
        Assert.That(Notes(result)[0].TickLength, Is.EqualTo(77)); // Preserve native length at 1920 PPQ.
        var phrases = XmkImporter.Import(Bytes(new[] { Tempo }.Concat(new uint[] { 12, 13, 14, 15, 22, 23 }
            .Select((type, i) => new Entry(i, type, .5f))).ToArray()));
        Assert.That(phrases.Chart.Dj.GetDifficulty(Difficulty.Expert).Phrases.Select(p => p.Type), Is.EqualTo(new[] {
            PhraseType.DjEffectGreen, PhraseType.DjEffectBlue, PhraseType.DjEffectRed, PhraseType.StarPower,
            PhraseType.DjEffectAll, PhraseType.DjCrossfadeNull }));
    }

    [Test]
    public void Djh1DialectIsExplicitAndTempoCanComeFromMetadata()
    {
        var result = XmkImporter.Import(Bytes(new[] { new Entry(0, 48, .5f), new Entry(1, 49, .5f), new Entry(2, 50, .5f),
            new Entry(3, 51, .5f), new Entry(4, 11, .0625f) }, version: 1),
            new XmkImportOptions { Game = XmkGame.DjHero1, FallbackBpm = 100 });
        Assert.That(Notes(result).Select(n => n.Type), Is.EqualTo(new[] { DjNoteType.GreenScratchZone, DjNoteType.BlueScratchZone, DjNoteType.CrossfadeGreen }));
        Assert.That(Notes(result).Last().IsSpike, Is.True);
        Assert.That(Notes(result)[1].Time, Is.EqualTo(2.4).Within(1e-9));
        Assert.That(result.Diagnostics.Single(), Does.Contain("caller metadata"));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { new Entry(0, 0) })));
    }

    [Test]
    public void TempoChangesPreserveMeasurePositionsAndIntervalEnds()
    {
        var result = XmkImporter.Import(Bytes(new[] { new Entry(2, 0), new Entry(1, 0x0B000001, 0, 1000000),
            Tempo, new Entry(.5f, 20, 1.5f), new Entry(1, 0x0B000002, 0, BitConverter.SingleToUInt32Bits(999)) }));
        var notes = Notes(result);
        Assert.That(notes[0].Time, Is.EqualTo(1));
        Assert.That(notes[0].TimeEnd, Is.EqualTo(6));
        Assert.That(notes[1].Time, Is.EqualTo(6));
        Assert.That(result.Chart.SyncTrack.Tempos.Select(t => t.BeatsPerMinute), Is.EqualTo(new[] { 120.0, 60.0 }));
        Assert.That(result.Chart.SyncTrack.Beatlines.Count, Is.GreaterThan(8));
    }

    [Test]
    public void NominalBpmFallbackIsReported()
    {
        var result = XmkImporter.Import(Bytes(new[] { new Entry(0, 0x0B000002, 0, BitConverter.SingleToUInt32Bits(150)), new Entry(1, 0) }));
        Assert.That(Notes(result)[0].Time, Is.EqualTo(1.6));
        Assert.That(result.Diagnostics.Single(), Does.Contain("nominal"));
    }

    [Test]
    public void StringsEffectsSamplesAndUnsupportedDataSurviveConversion()
    {
        byte[] strings = Encoding.UTF8.GetBytes("Synthetic Author\0Intro\0custom marker\0");
        var entries = new[] { Tempo, new Entry(0, 0x0AFFFFFF, 0, 8 * 16), new Entry(.25f, 0x09FFFFFF, 0, 8 * 16 + 17),
            new Entry(.5f, 0x0B000000, 0, 8 * 16 + 23), new Entry(.75f, 0x06000009), new Entry(1, 16, 1, 7),
            new Entry(2, 0x12345678, .5f, 0xABCDEF), new Entry(0, uint.MaxValue) };
        var result = XmkImporter.Import(Bytes(entries, strings));
        Assert.That(result.Authors.Single(), Is.EqualTo("Synthetic Author"));
        Assert.That(result.Chart.Sections.Single().Name, Is.EqualTo("Intro"));
        Assert.That(result.Chart.Dj.GetDifficulty(Difficulty.Expert).TextEvents.Select(e => e.Text),
            Is.EqualTo(new[] { "custom marker", "dj_effect delay", "dj_sample 7" }));
        Assert.That(result.UnsupportedEvents.Single().Extra, Is.EqualTo(0xABCDEF));
    }

    [Test]
    public void ChordsLinksDuplicatesAndEuphoriaEndExclusive()
    {
        var result = XmkImporter.Import(Bytes(new[] { Tempo, new Entry(0, 15, 1), new Entry(0, 0), new Entry(0, 1),
            new Entry(0, 0, 2), new Entry(1, 2) }));
        var notes = result.Chart.Dj.GetDifficulty(Difficulty.Expert).Notes;
        Assert.That(notes.Count, Is.EqualTo(2));
        Assert.That(notes[0].ChildNotes.Count, Is.EqualTo(1));
        Assert.That(notes[0].IsStarPowerStart && notes[0].IsStarPowerEnd, Is.True);
        Assert.That(notes[0].ChildNotes[0].IsStarPower, Is.True);
        Assert.That(notes[1].IsStarPower, Is.False);
        Assert.That(notes[0].NextNote, Is.SameAs(notes[1]));
        Assert.That(notes[1].PreviousNote, Is.SameAs(notes[0]));
        Assert.That(result.Diagnostics.Single(), Does.Contain("Duplicate"));
    }

    [Test]
    public void FileLoaderSelectsDifficultyAndImporterCanOverrideIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "yarg-dj-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "DJ_Hard.xmk");
        try
        {
            File.WriteAllBytes(path, Bytes(new[] { Tempo, new Entry(1, 0) }));
            Assert.That(SongChart.FromFile(ParseSettings.Default_Chart, path).Dj.GetDifficulty(Difficulty.Hard).Notes.Count, Is.EqualTo(1));
            Assert.That(XmkImporter.FromFile(path, new XmkImportOptions { Difficulty = Difficulty.Easy }).Chart.Dj.GetDifficulty(Difficulty.Easy).Notes.Count, Is.EqualTo(1));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Test]
    public void ChecksumCoversDeclaredDataAndExcludesPadding()
    {
        byte[] bytes = Bytes(new[] { Tempo, new Entry(1, 0) }, new byte[] { 0 });
        bytes[^1] = 1;
        Assert.That(XmkImporter.Import(bytes).ChecksumValid, Is.True);
        bytes[4] ^= 1;
        Assert.That(XmkImporter.Import(bytes).ChecksumValid, Is.False);
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(bytes, new XmkImportOptions { RequireValidChecksum = true }));
    }

    [TestCase(0)] [TestCase(1)] [TestCase(15)] [TestCase(31)] [TestCase(47)]
    public void TruncatedStructuresAreRejected(int length)
    {
        var bytes = Bytes(new[] { Tempo, new Entry(1, 0) });
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(bytes.AsSpan(0, length)));
    }

    [TestCase(float.NaN, 0)] [TestCase(float.PositiveInfinity, 0)] [TestCase(-1, 0)]
    [TestCase(0, float.NaN)] [TestCase(0, -1)] [TestCase(10000, 1)]
    public void InvalidEventTimesAreRejected(float start, float length) =>
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo, new Entry(start, 0, length) })));

    [Test]
    public void CountsVersionsPointersEncodingAndTempoAreValidated()
    {
        var bytes = Bytes(new[] { Tempo });
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(bytes));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo }, version: 3)));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { new Entry(0, 0x0B000001) })));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo, new Entry(0, 0x0AFFFFFF, 0, 31) }, new byte[] { 0 })));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo, new Entry(0, 0x0AFFFFFF, 0, 33) }, new byte[] { 0 })));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo, new Entry(0, 0x0AFFFFFF, 0, 32) }, new byte[] { 65 })));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo, new Entry(0, 0x0AFFFFFF, 0, 32) }, new byte[] { 255, 0 })));
        Assert.Throws<InvalidDataException>(() => XmkImporter.Import(Bytes(new[] { Tempo, new Entry(0, 0x0B000001, 0, 600000) })));
    }

    [Test, Explicit("Local-only customs; set YARG_DJ_TEST_CORPUS and select this test explicitly.")]
    public void LocalCorpus()
    {
        string? corpus = Environment.GetEnvironmentVariable("YARG_DJ_TEST_CORPUS");
        Assert.That(corpus, Is.Not.Null.And.Not.Empty);
        var paths = Directory.GetFiles(corpus!, "*.xmk", SearchOption.AllDirectories).Order().ToArray();
        Assert.That(paths, Is.Not.Empty);
        foreach (string path in paths)
        {
            var result = XmkImporter.FromFile(path);
            var track = result.Chart.Dj.GetDifficulty(Difficulty.Expert);
            Assert.That(Notes(result), Is.Not.Empty);
            Assert.That(Notes(result).All(n => double.IsFinite(n.TimeEnd)), Is.True);
            TestContext.Out.WriteLine($"{Path.GetRelativePath(corpus!, path)}: CRC={result.ChecksumValid}, components={Notes(result).Count}, groups={track.Notes.Count}, phrases={track.Phrases.Count}, tempos={result.Chart.SyncTrack.Tempos.Count}, unsupported={result.UnsupportedEvents.Count}, diagnostics={result.Diagnostics.Count}");
            foreach (var diagnostic in result.Diagnostics.Distinct().Take(8)) TestContext.Out.WriteLine(diagnostic);
            var engine = new DjEngine(track.Clone(), result.Chart.SyncTrack, new EnginePreset.DjPreset().Create(), true);
            engine.Update(track.GetEndTime() + 2);
            TestContext.Out.WriteLine($"Bot: hits={engine.EngineStats.NotesHit}/{engine.EngineStats.TotalNotes}, misses={engine.EngineStats.Misses}, score={engine.EngineStats.TotalScore}, coverage={engine.EngineStats.CoverageUnitsHit}/{engine.EngineStats.CoverageUnitsTotal}");
            Assert.That(engine.EngineStats.Misses, Is.Zero);
        }
    }
}
