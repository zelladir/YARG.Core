using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using NUnit.Framework;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Core.Song.Cache;
using TextEvent = Melanchall.DryWetMidi.Core.TextEvent;

namespace YARG.Core.UnitTests.Chart;

public class DjBandTests
{
    private string _root = null!;
    private static readonly string FakeHash = new('a', 64);

    [SetUp] public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "yarg-dj-band-" + Guid.NewGuid())).FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, true);

    private static IEnumerable<(long Tick, MidiEvent Event)> Note(long tick, int pitch, long length = 30)
    {
        yield return (tick, new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)100));
        yield return (tick + length, new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0));
    }
    private static TrackChunk Track(string name, IEnumerable<(long Tick, MidiEvent Event)> events)
    {
        var track = new TrackChunk(new SequenceTrackNameEvent(name));
        long last = 0;
        foreach (var e in events.OrderBy(e => e.Tick)) { e.Event.DeltaTime = e.Tick - last; track.Events.Add(e.Event); last = e.Tick; }
        return track;
    }
    private static MidiFile Source(bool sections = true, bool drums = true, long tempo = 500000)
    {
        var tracks = new List<TrackChunk>
        {
            Track("TEMPO", new (long, MidiEvent)[] { (0, new SetTempoEvent(tempo)), (0, new TimeSignatureEvent(4, 4)), (5760, new SetTempoEvent(400001)) }),
            Track("PART GUITAR", new[] { 480L, 960, 2880, 3600, 3720, 3840, 3960, 4080, 5760, 6240, 6720, 8640 }
                .SelectMany(t => Note(t, 96)).Concat(Note(5760, 103, 960)).Concat(Note(0, 116, 1440))),
            Track("PART BASS", new[] { 1440L, 3360, 4800, 7200 }.SelectMany(t => Note(t, 96))),
            Track("PART KEYS", Note(4320, 96)),
            Track("PART REAL_KEYS_X", Note(5280, 60, 240)),
            Track("PART VOCALS", Note(1080, 105, 240).Concat(Note(1080, 60, 120))),
        };
        if (drums) tracks.Add(Track("PART DRUMS", Note(0, 96).Concat(Note(240, 97)).Concat(Note(1680, 100))
            .Concat(Note(1920, 120, 480)).Concat(Note(1920, 121, 480)).Concat(Note(1920, 122, 480))
            .Concat(Note(1920, 123, 480)).Concat(Note(1920, 124, 480)).Concat(Note(2400, 96))));
        var ev = new List<(long, MidiEvent)> { (7680, new TextEvent("[coda]")), (9000, new TextEvent("[end]")) };
        if (sections) ev.AddRange(new (long, MidiEvent)[] { (0, new TextEvent("[section verse]")), (2880, new TextEvent("[section chorus]")), (5760, new TextEvent("[section guitar_solo]")) });
        tracks.Add(Track("EVENTS", ev));
        return new MidiFile(tracks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
    }
    private static SongChart Read(MidiFile midi) => SongChart.FromMidi(ParseSettings.Default_Midi, midi);
    private static List<DjNote> Notes(SongChart chart, Difficulty difficulty = Difficulty.Expert) => chart.Dj.GetDifficulty(difficulty).Notes.SelectMany(n => new[] { n }.Concat(n.ChildNotes)).ToList();
    private static SongChart Generated(SongChart source)
    {
        var result = DjBandGenerator.Generate(source, FakeHash);
        Assert.That(result.Info.Flags & (DjQualityFlags.ConstraintViolation | DjQualityFlags.SyncMismatch), Is.EqualTo(DjQualityFlags.None));
        Assert.That(DjSidecar.Merge(source, result.Text, FakeHash).Accepted, Is.True);
        return source;
    }
    private string WriteSource(MidiFile? midi = null)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "song")).FullName;
        (midi ?? Source()).Write(Path.Combine(folder, "notes.mid"), overwriteFile: true);
        File.WriteAllText(Path.Combine(folder, "song.ini"), "[song]\nname = Synthetic band\nartist = Test\ndelay = 123\nsong_length = 10000\n");
        File.WriteAllBytes(Path.Combine(folder, "song.ogg"), [0]);
        return folder;
    }

    [Test] public void SyntheticMidiMapsFoundationMelodyAndAccents()
    {
        var source = Generated(Read(Source()));
        var notes = Notes(source);
        Assert.That(notes.Any(n => n.Type == DjNoteType.GreenTap && n.Tick == 0), Is.True, "kick");
        Assert.That(notes.Any(n => n.Type == DjNoteType.BlueTap && n.Tick == 480), Is.True, "guitar");
        Assert.That(notes.Any(n => n.Type == DjNoteType.GreenTap && n.Tick == 1440), Is.True, "bass");
        Assert.That(notes.Any(n => n.Type == DjNoteType.BlueTap && n.Tick == 4320), Is.True, "keys");
        Assert.That(notes.Any(n => n.Type == DjNoteType.BlueTap && n.Tick == 5280), Is.True, "pro keys");
        Assert.That(notes.Any(n => n.Type == DjNoteType.RedTap && n.Tick == 1080), Is.True, "vocal phrase");
    }

    [Test] public void RepetitionFillsOverdriveSoloAndCodaMapToDjMechanics()
    {
        var chart = Generated(Read(Source())); var notes = Notes(chart);
        Assert.That(notes.Any(n => n.IsScratch), Is.True);
        Assert.That(notes.Where(n => n.IsScratch).Select(n => n.Type), Does.Contain(DjNoteType.BlueScratchUp));
        Assert.That(notes.Any(n => n.IsScratchZone && n.Tick == 1920), Is.True);
        Assert.That(notes.Any(n => n.IsCrossfade), Is.True);
        Assert.That(notes.Any(n => n.Type == DjNoteType.FreestyleCrossfade && n.Tick == 7680), Is.True);
        Assert.That(notes.Any(n => !n.IsFreestyle && n.Tick >= 7680), Is.False);
        Assert.That(chart.Dj.GetDifficulty(Difficulty.Expert).Phrases.Any(p => p.Type == PhraseType.StarPower), Is.True);
        Assert.That(chart.Dj.GetDifficulty(Difficulty.Expert).Phrases.Any(p => p.Type == PhraseType.DjEffectAll), Is.True);
    }

    [Test] public void RoundTripPreservesExactMidiTimingAndOffsetIsNotAppliedTwice()
    {
        string folder = WriteSource(Source(tempo: 500003));
        var result = DjBandGenerator.GenerateFolder(folder);
        File.WriteAllText(Path.Combine(folder, "dj.chart"), result.Text);
        var raw = DjBandGenerator.LoadSource(Path.Combine(folder, "notes.mid"));
        var loaded = SongChart.FromFile(ParseSettings.Default_Midi, Path.Combine(folder, "notes.mid"));
        Assert.That(loaded.DjSidecar!.Accepted, Is.True);
        Assert.That(DjSidecar.SyncMatches(raw.SyncTrack, loaded.SyncTrack), Is.True);
        foreach (var n in Notes(loaded)) Assert.That(n.Time, Is.EqualTo(raw.SyncTrack.TickToTime(n.Tick)));
        var entry = Scan(folder);
        Assert.That(entry.LoadChart()!.Dj.IsEmpty, Is.False);
        Assert.That(entry.SongOffsetMilliseconds, Is.EqualTo(123));
        Assert.That(Notes(entry.LoadChart()!)[0].Time, Is.Zero);
    }

    [TestCase("B 120000", "B 130000")]
    [TestCase("Resolution = 480", "Resolution = 960")]
    [TestCase("TS 4 2", "TS 3 2")]
    [TestCase("Offset = 0", "Offset = 1")]
    public void SyncMismatchRejectsOnlyDjPart(string from, string to)
    {
        var source = Read(Source());
        string text = DjBandGenerator.Generate(source, FakeHash).Text.Replace(from, to);
        var info = DjSidecar.Merge(source, text, FakeHash);
        Assert.That(info.Accepted, Is.False);
        Assert.That(info.Flags.HasFlag(DjQualityFlags.SyncMismatch), Is.True);
        Assert.That(info.Hidden, Is.True); Assert.That(source.Dj.IsEmpty, Is.True); Assert.That(source.FiveFretGuitar.IsEmpty, Is.False);
    }

    [Test] public void ChangedSourceHashRejected()
    {
        var source = Read(Source()); var result = DjBandGenerator.Generate(source, FakeHash);
        Assert.That(DjSidecar.Merge(source, result.Text, new string('b', 64)).Flags.HasFlag(DjQualityFlags.SourceChanged), Is.True);
    }

    [Test] public void RejectedReloadDoesNotRetainThePreviouslyMergedDjPart()
    {
        var source = Read(Source()); var result = DjBandGenerator.Generate(source, FakeHash);
        Assert.That(DjSidecar.Merge(source, result.Text, FakeHash).Accepted, Is.True);
        Assert.That(DjSidecar.Merge(source, result.Text.Replace("B 120000", "B 130000"), FakeHash).Accepted, Is.False);
        Assert.That(source.Dj.IsEmpty, Is.True);
    }

    [Test] public void DeterministicAcrossCultureAndStemOrder()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var source = Read(Source());
            string first = DjBandGenerator.Generate(source, FakeHash, ["song.ogg", "guitar.ogg", "drums.ogg"]).Text;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string second = DjBandGenerator.Generate(Read(Source()), FakeHash, ["drums.ogg", "guitar.ogg", "song.ogg"]).Text;
            Assert.That(second, Is.EqualTo(first)); Assert.That(second.Contains('\r'), Is.False);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [TestCase(250000)] [TestCase(500000)] [TestCase(1300000)]
    public void AllDifficultiesAreMonotonicAndPlayable(long tempo)
    {
        var chart = Generated(Read(Source(tempo: tempo)));
        Assert.That(DjBandGenerator.Validate(chart), Is.Empty);
        foreach (var difficulty in new[] { Difficulty.Beginner, Difficulty.Easy, Difficulty.Medium })
            Assert.That(Notes(chart, difficulty).Any(n => n.IsScratch || n.IsScratchZone), Is.False);
    }

    [Test] public void MissingSectionsSparseAndIrregularSourcesAreFlaggedAndHidden()
    {
        var midi = new MidiFile(Track("TEMPO", new (long, MidiEvent)[] { (0, new SetTempoEvent(2000000)), (0, new TimeSignatureEvent(7, 8)) }),
            Track("PART GUITAR", Note(480, 96))) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var result = DjBandGenerator.Generate(Read(midi), FakeHash);
        Assert.That(result.Info.Flags.HasFlag(DjQualityFlags.MissingSections), Is.True);
        Assert.That(result.Info.Flags.HasFlag(DjQualityFlags.SparseSource), Is.True);
        Assert.That(result.Info.Flags.HasFlag(DjQualityFlags.IrregularTempo), Is.True);
        Assert.That(result.Info.Hidden, Is.True);
    }

    [TestCase("hand-authored")]
    [TestCase("[Events]\n{\n0 = E \"DjGenerator = YARG.djgen\"\n}\n")]
    [TestCase("[Song]\n{\nDjGenerator = \"someone-else\"\n}\n")]
    public void HandAuthoredSidecarsAreNeverReplaced(string original)
    {
        string path = Path.Combine(_root, "dj.chart"); File.WriteAllText(path, original);
        string generated = DjBandGenerator.Generate(Read(Source()), FakeHash).Text;
        Assert.That(DjSidecar.WriteGenerated(path, generated), Is.False);
        Assert.That(File.ReadAllText(path), Is.EqualTo(original));
    }

    [Test] public void GeneratorOwnedSidecarsCanBeRegenerated()
    {
        string path = Path.Combine(_root, "dj.chart");
        string first = DjBandGenerator.Generate(Read(Source()), FakeHash).Text;
        string second = DjBandGenerator.Generate(Read(Source(sections: false)), FakeHash).Text;
        Assert.That(DjSidecar.WriteGenerated(path, first), Is.True);
        Assert.That(DjSidecar.WriteGenerated(path, second), Is.True);
        Assert.That(File.ReadAllText(path), Is.EqualTo(second));
        Assert.That(Directory.GetFiles(_root, "*.tmp"), Is.Empty);
    }

    private static UnpackedIniEntry Scan(string folder)
    {
        var result = UnpackedIniEntry.ProcessNewEntry(folder, new FileInfo(Path.Combine(folder, "notes.mid")), ChartFormat.Mid,
            new FileInfo(Path.Combine(folder, "song.ini")), "test");
        Assert.That(result.HasValue, Is.True); return result.Value;
    }

    [Test] public void ScannerDiscoversHiddenMetadataAndKeepsIndependentBeginner()
    {
        string folder = WriteSource(); var result = DjBandGenerator.GenerateFolder(folder);
        string text = result.Text.Replace("DjQuality = 100", "DjQuality = 20").Replace("DjHidden = false", "DjHidden = true");
        File.WriteAllText(Path.Combine(folder, "dj.chart"), text);
        var entry = Scan(folder);
        Assert.That(entry.HasDjPart(true), Is.True); Assert.That(entry.HasInstrument(Instrument.DjSingle), Is.False);
        Assert.That(entry.DjHidden, Is.True); Assert.That(entry[Instrument.DjSingle][Difficulty.Beginner], Is.True);
        Assert.Throws<NotSupportedException>(() => entry.GetLeaderboardHash(Instrument.DjSingle, Difficulty.Expert));
    }

    [Test] public void CacheRoundTripAndFullScanDetectSidecarAddEditAndDelete()
    {
        string folder = WriteSource();
        string cache = Path.Combine(_root, "cache.bin"), bad = Path.Combine(_root, "bad.txt");
        SongEntry Run(bool quick = false) => CacheHandler.RunScan(quick, cache, bad, false, new List<string> { folder }).Entries.Values.SelectMany(v => v).Single();
        Assert.That(Run().HasDjPart(true), Is.False);
        string sidecar = Path.Combine(folder, "dj.chart");
        var result = DjBandGenerator.GenerateFolder(folder); File.WriteAllText(sidecar, result.Text);
        var entry = Run(); Assert.That(entry.HasInstrument(Instrument.DjSingle), Is.True);
        Assert.That(Run(true).DjQuality, Is.EqualTo(result.Info.Quality));
        DateTime stamp = File.GetLastWriteTimeUtc(sidecar);
        File.WriteAllText(sidecar, result.Text.Replace("B 120000", "B 121000")); File.SetLastWriteTimeUtc(sidecar, stamp);
        Assert.That(Run().DjFlags.HasFlag(DjQualityFlags.SyncMismatch), Is.True);
        File.Delete(sidecar); Assert.That(Run().HasDjPart(true), Is.False);
    }

    [Test] public void HandAuthoredEasyDoesNotInventBeginner()
    {
        string folder = WriteSource();
        var source = Read(Source());
        string generated = DjBandGenerator.Generate(source, FakeHash).Text;
        string header = generated[..generated.IndexOf("[BeginnerDJTurntable]", StringComparison.Ordinal)].Replace("DjGenerator = \"YARG.djgen\"", "Author = \"Test\"");
        File.WriteAllText(Path.Combine(folder, "dj.chart"), header + "[EasyDJTurntable]\n{\n0 = N 0 0\n}\n");
        var entry = Scan(folder);
        Assert.That(entry[Instrument.DjSingle][Difficulty.Easy], Is.True);
        Assert.That(entry[Instrument.DjSingle][Difficulty.Beginner], Is.False);
        Assert.That(entry.DjQuality, Is.EqualTo(-1));
    }

    [TestCase("DRUMS_4.ogg", DjStemGroup.Beat)] [TestCase("bass.opus", DjStemGroup.Beat)]
    [TestCase("rhythm.ogg", DjStemGroup.Beat)] [TestCase("vocals_1_clean.ogg", DjStemGroup.Melodic)]
    [TestCase("keys.ogg", DjStemGroup.Melodic)] [TestCase("guitar.ogg", DjStemGroup.Melodic)]
    [TestCase("backing.ogg", DjStemGroup.Backing)] [TestCase("song.ogg", DjStemGroup.Backing)]
    [TestCase("crowd.ogg", DjStemGroup.Excluded)] [TestCase("unknown.ogg", DjStemGroup.Excluded)]
    [TestCase("song.ini", DjStemGroup.Excluded)]
    public void StemClassification(string file, DjStemGroup group) => Assert.That(DjAudioRouting.ClassifyFile(file), Is.EqualTo(group));

    [Test] public void UnclaimedModeProtectsEveryHumanClaimAndDefaultsToNoVocals()
    {
        var claims = DjAudioRouting.HumanClaims([Instrument.ProDrums, Instrument.FiveFretGuitar, Instrument.Keys, Instrument.FiveFretBass, Instrument.FiveFretRhythm, Instrument.Harmony]);
        foreach (var stem in claims) Assert.That(DjAudioRouting.Gain(stem, DjAudioMode.UnclaimedStems, claims, 1, missed: true, allowVocals: true), Is.EqualTo(1));
        Assert.That(DjAudioRouting.Gain(SongStem.Song, DjAudioMode.UnclaimedStems, claims, 1, missed: true), Is.Zero);
        Assert.That(DjAudioRouting.Gain(SongStem.Vocals, DjAudioMode.UnclaimedStems, [], 1, missed: true), Is.EqualTo(1));
        Assert.That(DjAudioRouting.Gain(SongStem.Guitar, DjAudioMode.UnclaimedStems, [], -1), Is.Zero);
        Assert.That(DjAudioRouting.Gain(SongStem.Bass, DjAudioMode.UnclaimedStems, [], 0), Is.EqualTo(1));
    }

    [Test] public void WholeMixColorNeverMutesAndGameplayOnlyIsIdentity()
    {
        foreach (SongStem stem in Enum.GetValues<SongStem>())
        {
            Assert.That(DjAudioRouting.Gain(stem, DjAudioMode.WholeMixColor, [], 1, missed: true, allowVocals: true), Is.GreaterThan(0));
            Assert.That(DjAudioRouting.Gain(stem, DjAudioMode.GameplayOnly, [], -1, missed: true), Is.EqualTo(1));
        }
        Assert.That(DjAudioRouting.Gain(SongStem.Bass, DjAudioMode.WholeMixColor, [], 1), Is.EqualTo(Math.Pow(10, -.3)).Within(1e-12));
    }

    [Test] public void BandAndStandalonePresetsAreSeparateCopies()
    {
        Assert.That(new EnginePreset("test").Dj.AutoFadeEuphoria, Is.False);
        Assert.That(EnginePreset.DjPreset.StandaloneDjHero2().AutoFadeEuphoria, Is.True);
        var preset = EnginePreset.DjPreset.Band(); preset.ScratchActivate = 10;
        Assert.That(EnginePreset.DjPreset.Band().ScratchActivate, Is.EqualTo(6));
    }

    [Test] public void CoopHumanOwnsRhythmAndWholeMixIncludesVocals()
    {
        var claims = DjAudioRouting.HumanClaims([Instrument.FiveFretCoopGuitar]);
        Assert.That(claims, Does.Contain(SongStem.Rhythm));
        Assert.That(DjAudioRouting.Gain(SongStem.Rhythm, DjAudioMode.UnclaimedStems, claims, 1, true), Is.EqualTo(1));
        Assert.That(DjAudioRouting.Gain(SongStem.Vocals, DjAudioMode.WholeMixColor, [SongStem.Vocals], 1), Is.GreaterThan(1));
    }

    [Test] public void DenseOppositeLaneKeepsVerseCrossfadeCentered()
    {
        var midi = new MidiFile(Track("TEMPO", [(0, new SetTempoEvent(500000))]),
            Track("EVENTS", [(0, new TextEvent("[section verse]"))]),
            Track("PART GUITAR", Enumerable.Range(0, 16).SelectMany(i => Note(i * 240, 96))))
            { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var chart = Generated(Read(midi));
        Assert.That(Notes(chart).Where(n => n.IsCrossfade).All(n => n.Type == DjNoteType.CrossfadeCenter), Is.True);
    }

    [Test] public void RubatoAndExtremeDensityFlagsAreIndependent()
    {
        var midi = new MidiFile(Track("TEMPO", Enumerable.Range(0, 50).Select(i => ((long)i * 120, (MidiEvent)new SetTempoEvent(490000 + 100 * i)))),
            Track("PART GUITAR", Enumerable.Range(0, 480).SelectMany(i => Note(i * 12, 96, 6))))
            { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var result = DjBandGenerator.Generate(Read(midi), FakeHash);
        Assert.That(result.Info.Flags.HasFlag(DjQualityFlags.RubatoTempo), Is.True);
        Assert.That(result.Info.Flags.HasFlag(DjQualityFlags.ExtremeDensity), Is.True);
    }

    [TestCase("0 = B invalid")]
    [TestCase("0 = B 120000\n0 = B 120000")]
    [TestCase("0 = B 120000\n0 = A 10")]
    public void InvalidSyncRecordsCannotDisappearDuringPermissiveParsing(string sync)
    {
        var source = Read(Source());
        string text = DjBandGenerator.Generate(source, FakeHash).Text.Replace("0 = B 120000", sync);
        Assert.That(DjSidecar.Merge(source, text, FakeHash).Flags.HasFlag(DjQualityFlags.SyncMismatch), Is.True);
    }

    [Test] public void EmptyMidiProducesHiddenNoPlayablePart()
    {
        var source = Read(new MidiFile(Track("TEMPO", [(0, new SetTempoEvent(500000))])) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) });
        var result = DjBandGenerator.Generate(source, FakeHash);
        Assert.That(result.Info.Hidden, Is.True);
        Assert.That(result.Info.Flags.HasFlag(DjQualityFlags.NoPlayableNotes), Is.True);
        Assert.That(DjSidecar.Merge(source, result.Text, FakeHash).Difficulties, Is.EqualTo(DifficultyMask.None));
    }

    [Test] public void CorruptSidecarTickIsRejectedBeforeBeatlineExpansion()
    {
        var source = Read(Source());
        string text = DjBandGenerator.Generate(source, FakeHash).Text.Replace("0 = N 0 0", "4294967295 = N 0 0");
        Assert.That(DjSidecar.Merge(source, text, FakeHash).Flags.HasFlag(DjQualityFlags.InvalidSidecar), Is.True);
        Assert.That(source.FiveFretGuitar.IsEmpty, Is.False);
    }

    [TestCase(Difficulty.Beginner)] [TestCase(Difficulty.Easy)] [TestCase(Difficulty.Medium)]
    [TestCase(Difficulty.Hard)] [TestCase(Difficulty.Expert)]
    public void GeneratedTiersRunThroughHeadlessBandEngine(Difficulty difficulty)
    {
        var chart = Generated(Read(Source()));
        var engine = new YARG.Core.Engine.Dj.DjEngine(chart.Dj.GetDifficulty(difficulty), chart.SyncTrack, EnginePreset.DjPreset.Band().Create(), true);
        engine.Update(chart.GetEndTime() + 2);
        Assert.That(engine.EngineStats.Misses, Is.Zero);
        Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(Notes(chart, difficulty).Count(n => !n.IsFreestyle)));
    }

    [Test, Explicit("Requires the owner's local-only 64-song band corpus.")]
    public void LocalBandCorpus()
    {
        string? path = Environment.GetEnvironmentVariable("YARG_DJ_BAND_CORPUS");
        Assert.That(path, Is.Not.Null);
        var files = Directory.GetFiles(path!, "notes.mid", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.That(files, Has.Length.EqualTo(64));
        foreach (var file in files)
        {
            var generated = DjBandGenerator.GenerateFolder(Path.GetDirectoryName(file)!);
            var source = DjBandGenerator.LoadSource(file);
            Assert.That(DjSidecar.Merge(source, generated.Text, DjSidecar.HashFile(file)).Accepted, Is.True, file);
            Assert.That(DjBandGenerator.Validate(source), Is.Empty, file);
            Assert.That(source.Dj.HasDifficulty(Difficulty.Beginner), Is.True, file);
        }
    }
}
