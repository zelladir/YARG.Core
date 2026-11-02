using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Replays;

namespace YARG.Core.UnitTests.Chart;

public class DjChartTests
{
    private const string HEADER = "[Song]\n{\n  Resolution = 192\n}\n[SyncTrack]\n{\n  0 = B 120000\n}\n";

    private static string Section(string name, string body) => $"[{name}]\n{{\n{body}\n}}\n";
    private static SongChart Read(string body, string difficulty = "Expert") =>
        SongChart.FromDotChart(ParseSettings.Default_Chart, HEADER + Section(difficulty + "DJTurntable", body));

    private static List<DjNote> Flatten(InstrumentDifficulty<DjNote> difficulty) =>
        difficulty.Notes.SelectMany(note => new[] { note }.Concat(note.ChildNotes)).ToList();

    [TestCase("Beginner", Difficulty.Beginner)]
    [TestCase("Easy", Difficulty.Easy)]
    [TestCase("Medium", Difficulty.Medium)]
    [TestCase("Hard", Difficulty.Hard)]
    [TestCase("Expert", Difficulty.Expert)]
    public void LoadsEachProposedDifficulty(string section, Difficulty difficulty)
    {
        var song = Read("192 = N 0 0", section);
        var track = song.Dj.GetDifficulty(difficulty);
        Assert.That(track.Instrument, Is.EqualTo(Instrument.DjSingle));
        Assert.That(track.Notes.Single().Time, Is.EqualTo(0.5));
    }

    [Test]
    public void BeginnerAndEasyRemainIndependent()
    {
        var song = SongChart.FromDotChart(ParseSettings.Default_Chart, HEADER +
            Section("BeginnerDJTurntable", "0 = N 0 0") + Section("EasyDJTurntable", "192 = N 2 0"));
        Assert.That(song.Dj.GetDifficulty(Difficulty.Beginner).Notes.Single().Type, Is.EqualTo(DjNoteType.GreenTap));
        Assert.That(song.Dj.GetDifficulty(Difficulty.Easy).Notes.Single().Type, Is.EqualTo(DjNoteType.BlueTap));
    }

    [Test]
    public void AllSingleTableNoteTypesArePreserved()
    {
        var types = Enum.GetValues<DjNoteType>();
        string body = string.Join('\n', types.Select((type, index) => $"{index * 192} = N {(int) type} 1"));
        var notes = Flatten(Read(body).Dj.GetDifficulty(Difficulty.Expert));
        Assert.That(notes.Select(note => note.Type), Is.EqualTo(types));
        Assert.That(notes.All(note => note.TickLength == 1), Is.True);
        Assert.That(notes.Single(note => note.Type == DjNoteType.OpenScratchUp).Lane, Is.EqualTo(DjLane.Open));
    }

    [Test]
    public void UnorderedNotesBecomeChordsWithLinksWithoutSnapping()
    {
        var settings = ParseSettings.Default_Chart;
        settings.NoteSnapThreshold = 20;
        settings.SustainCutoffThreshold = 100;
        var song = SongChart.FromDotChart(settings, HEADER + Section("ExpertDJTurntable",
            "194 = N 4 1\n192 = N 0 0\n192 = N 1 0\n193 = N 2 1\n192 = N 64 1"));
        var notes = song.Dj.GetDifficulty(Difficulty.Expert).Notes;
        Assert.That(notes.Select(note => note.Tick), Is.EqualTo(new uint[] { 192, 193, 194 }));
        Assert.That(notes[0].ChildNotes.Count, Is.EqualTo(2));
        Assert.That(notes[0].ChildNotes[1].IsSpike, Is.True);
        Assert.That(notes[0].ChildNotes[1].TickLength, Is.EqualTo(1));
        Assert.That(notes[1].PreviousNote, Is.SameAs(notes[0]));
        Assert.That(notes[0].ChildNotes[0].NextNote, Is.SameAs(notes[1]));
        Assert.That(notes[2].TickLength, Is.EqualTo(1));
    }

    [TestCase(0, false, true)]
    [TestCase(48, false, true)]
    [TestCase(49, false, false)]
    [TestCase(48, true, false)]
    [TestCase(49, true, true)]
    public void CrossfadeSpikeThresholdAndSwap(int length, bool swap, bool expected)
    {
        var song = Read($"192 = N 64 {length}\n" + (swap ? "192 = N 67 0" : ""));
        var note = song.Dj.GetDifficulty(Difficulty.Expert).Notes.Single();
        Assert.That(note.IsSpike, Is.EqualTo(expected));
        Assert.That(note.TickLength, Is.EqualTo(length));
    }

    [Test]
    public void TempoChangesApplyToNotesPhrasesAndBounds()
    {
        var song = SongChart.FromDotChart(ParseSettings.Default_Chart,
            HEADER.Replace("0 = B 120000", "0 = B 120000\n192 = B 60000") +
            Section("ExpertDJTurntable", "96 = N 16 288\n96 = S 35 480\n576 = E \"dj_effect Filter\""));
        var track = song.Dj.GetDifficulty(Difficulty.Expert);
        Assert.That(track.Notes[0].Time, Is.EqualTo(0.25));
        Assert.That(track.Notes[0].TimeLength, Is.EqualTo(1.25));
        Assert.That(track.Phrases[0].TimeLength, Is.EqualTo(2.25));
        Assert.That(song.GetEndTime(), Is.EqualTo(2.5));
        Assert.That(song.GetLastTick(), Is.EqualTo(576));
        Assert.That(song.GetFirstNoteStartTime(), Is.EqualTo(0.25));
        Assert.That(song.SyncTrack.Beatlines, Is.Not.Empty);
    }

    [Test]
    public void ParsesPhrasesAndLocalEventsAndExcludesEuphoriaEndTick()
    {
        var song = Read("0 = S 2 384\n0 = S 32 48\n0 = S 33 48\n0 = S 34 48\n" +
            "0 = S 35 48\n0 = S 96 48\n0 = E \"dj_effect BeatRoll\"\n0 = E \"dj_sample 3 red\"\n" +
            "0 = N 0 0\n0 = N 1 0\n192 = N 4 0\n192 = N 2 0\n384 = N 0 0");
        var track = song.Dj.GetDifficulty(Difficulty.Expert);
        Assert.That(track.Phrases.Select(phrase => phrase.Type), Is.EqualTo(new[]
        {
            PhraseType.StarPower, PhraseType.DjEffectGreen, PhraseType.DjEffectRed,
            PhraseType.DjEffectBlue, PhraseType.DjEffectAll, PhraseType.DjCrossfadeNull,
        }));
        Assert.That(track.TextEvents.Select(ev => ev.Text), Is.EqualTo(new[] { "dj_effect BeatRoll", "dj_sample 3 red" }));
        Assert.That(track.Notes[0].IsStarPowerStart, Is.True);
        Assert.That(track.Notes[0].ChildNotes[0].IsStarPowerStart, Is.True);
        Assert.That(track.Notes[1].IsStarPowerEnd, Is.True);
        Assert.That(track.Notes[1].ChildNotes[0].IsStarPowerEnd, Is.True);
        Assert.That(track.Notes[2].IsStarPower, Is.False);
    }

    [Test]
    public void IgnoresMalformedUnknownAndSecondTableEventsWithoutLosingValidNotes()
    {
        var song = Read("-1 = N 0 0\n4294967296 = N 0 0\n0 = N 0 -1\n0 = N 0\n0 = N 23 0\n" +
            "0 = N 32 0\n0 = S 64 192\n0 = N 99999999999 0\n0 = S 2 4294967296\n" +
            "4294967295 = N 0 1\n0 = Z 0 0\n0 = N 0 0\n192 = N 1 0");
        var track = song.Dj.GetDifficulty(Difficulty.Expert);
        Assert.That(track.Notes.Select(note => note.Type), Is.EqualTo(new[] { DjNoteType.GreenTap, DjNoteType.RedTap }));
        Assert.That(track.Phrases, Is.Empty);
    }

    [Test]
    public void DuplicateNotesAndSectionsKeepFirstOccurrence()
    {
        var song = SongChart.FromDotChart(ParseSettings.Default_Chart, HEADER +
            Section("ExpertDJTurntable", "0 = N 64 48\n0 = N 64 96\n0 = N 67 0\n0 = N 67 0") +
            Section("ExpertDJTurntable", "192 = N 1 0"));
        var note = song.Dj.GetDifficulty(Difficulty.Expert).Notes.Single();
        Assert.That(note.TickLength, Is.EqualTo(48));
        Assert.That(note.IsSpike, Is.False);
        Assert.That(note.ChildNotes, Is.Empty);
    }

    [Test]
    public void ExactTrackNamesAndLegacyGuitarCanCoexist()
    {
        var song = SongChart.FromDotChart(ParseSettings.Default_Chart, HEADER +
            Section("ExpertDJDualTurntable", "0 = N 0 0") +
            Section("ExpertBogusDJTurntable", "0 = N 0 0") +
            Section("ExpertPlusDJTurntable", "0 = N 0 0") +
            Section("ExpertSingle", "192 = N 0 0"));
        Assert.That(song.Dj.IsEmpty, Is.True);
        Assert.That(song.FiveFretGuitar.GetDifficulty(Difficulty.Expert).Notes.Count, Is.EqualTo(1));
    }

    [Test]
    public void CloneAndResetPreserveDjDataAndOriginalFlags()
    {
        var source = Read("0 = S 2 192\n0 = N 0 0\n0 = N 64 48\n192 = N 2 0").Dj;
        source.GetDifficulty(Difficulty.Expert).Notes[0].WasHit = true;
        var clone = source.Clone();
        var notes = clone.GetDifficulty(Difficulty.Expert).Notes;
        Assert.That(notes[0], Is.Not.SameAs(source.GetDifficulty(Difficulty.Expert).Notes[0]));
        Assert.That(notes[0].WasHit, Is.False);
        Assert.That(notes[0].ChildNotes[0].IsSpike, Is.True);
        Assert.That(notes[0].NextNote, Is.SameAs(notes[1]));
        notes[0].Flags = NoteFlags.None;
        notes[0].ResetNoteState();
        Assert.That(notes[0].IsStarPower, Is.True);
    }

    [Test]
    public void LoadsFromFileAndAppendIncludesDjTrack()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".chart");
        try
        {
            File.WriteAllText(path, HEADER + Section("ExpertDJTurntable", "0 = N 0 0"));
            var loaded = SongChart.FromFile(ParseSettings.Default_Chart, path);
            var song = new SongChart(192);
            song.Append(loaded);
            Assert.That(song.Dj, Is.SameAs(loaded.Dj));
            Assert.That(song.Dj.GetDifficulty(Difficulty.Expert).Notes.Count, Is.EqualTo(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReservedEnumValuesAndModeMappingRemainStable()
    {
        Assert.That((byte) GameMode.Dj, Is.EqualTo(20));
        Assert.That((byte) Instrument.DjSingle, Is.EqualTo(50));
        Assert.That(Instrument.DjSingle.ToNativeGameMode(), Is.EqualTo(GameMode.Dj));
        Assert.That(GameMode.Dj.PossibleInstruments(), Is.EqualTo(new[] { Instrument.DjSingle }));
        Assert.That(Enum.IsDefined(typeof(Instrument), (byte) 51), Is.False);
        Assert.That((byte) Instrument.PartyVocals, Is.EqualTo(42));
        Assert.That((byte) Instrument.Band, Is.EqualTo(255));
    }

    [Test]
    public void LongOverlappingZonesAndChildNotesExtendChartBounds()
    {
        var song = Read("0 = N 0 0\n0 = N 16 768\n192 = N 1 0\n0 = S 32 960\n192 = S 34 48");
        var track = song.Dj.GetDifficulty(Difficulty.Expert);
        Assert.That(track.GetLastTick(), Is.EqualTo(960));
        Assert.That(track.GetLastNoteEndTime(), Is.EqualTo(2.0));
        Assert.That(track.GetEndTime(), Is.EqualTo(2.5));
        Assert.That(song.GetLastTick(), Is.EqualTo(960));
        Assert.That(song.GetLastNoteEndTime(), Is.EqualTo(2.0));
        Assert.That(song.GetEndTime(), Is.EqualTo(2.5));
    }

    [Test]
    public void DjReplayWriterRejectsBeforeWritingPayload()
    {
        var profile = new YargProfile { GameMode = GameMode.Dj, CurrentInstrument = Instrument.DjSingle };
        var frame = new ReplayFrame(profile, null!, null!, Array.Empty<GameInput>());
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        Assert.Throws<NotSupportedException>(() => frame.Serialize(writer));
        Assert.That(bytes.Length, Is.Zero);
    }

    [Test]
    public void DjReplayReaderRejectsUnsupportedPayloadExplicitly()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(new byte[] { (byte) 'R', (byte) 'P', (byte) 'F', (byte) 'M' });
        new YargProfile { GameMode = GameMode.Dj, CurrentInstrument = Instrument.DjSingle }.Serialize(writer);
        bytes.Position = 0;
        using var data = FixedArray.ReadRemainder(bytes);
        var stream = data.ToValueStream();
        Assert.Throws<NotSupportedException>(() => new ReplayFrame(ref stream, ReplayIO.REPLAY_VERSIONS.CURRENT));
    }

    [TestCase(GameMode.FiveFretGuitar, Instrument.FiveFretGuitar)]
    [TestCase(GameMode.SixFretGuitar, Instrument.SixFretGuitar)]
    [TestCase(GameMode.ProKeys, Instrument.ProKeys)]
    public void ExistingReplayFramesRoundTripByteForByte(GameMode mode, Instrument instrument)
    {
        var thresholds = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f };
        BaseEngineParameters parameters = mode == GameMode.ProKeys
            ? EnginePreset.Default.ProKeys.Create(thresholds, thresholds, false)
            : EnginePreset.Default.FiveFretGuitar.Create(thresholds, thresholds, false);
        BaseStats stats = mode == GameMode.ProKeys ? new KeysStats() : new GuitarStats();
        var profile = new YargProfile { GameMode = mode, CurrentInstrument = instrument, PreferredInstrument = instrument };
        var frame = new ReplayFrame(profile, parameters, stats, new[] { new GameInput(0.5, 0, 1) });
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        frame.Serialize(writer);
        bytes.Position = 0;
        using var data = FixedArray.ReadRemainder(bytes);
        var stream = data.ToValueStream();
        var loaded = new ReplayFrame(ref stream, ReplayIO.REPLAY_VERSIONS.CURRENT);
        Assert.That(loaded.Profile.GameMode, Is.EqualTo(mode));
        using var roundTrip = new MemoryStream();
        using var roundTripWriter = new BinaryWriter(roundTrip);
        loaded.Serialize(roundTripWriter);
        Assert.That(roundTrip.ToArray(), Is.EqualTo(bytes.ToArray()));
    }
}
