using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core.Engine.Dj;
using YARG.Core.Game;
using YARG.Core.Input;

namespace YARG.Core.UnitTests.Engine;

public class DjEngineTester
{
    private static DjEngine Create(string events, EnginePreset.DjPreset? preset = null, bool bot = false, string sync = "0 = B 120000")
    {
        var chart = SongChart.FromDotChart(ParseSettings.Default_Chart,
            "[Song]\n{\nResolution = 192\n}\n[SyncTrack]\n{\n" + sync +
            "\n}\n[ExpertDJTurntable]\n{\n" + events + "\n}\n");
        return new DjEngine(chart.Dj.GetDifficulty(Difficulty.Expert), chart.SyncTrack, (preset ?? new()).Create(), bot);
    }
    private static GameInput Button(double time, DjAction action, bool pressed = true) => GameInput.Create(time, action, pressed);
    private static GameInput Axis(double time, DjAction action, float value) => GameInput.Create(time, action, value);
    private static void Run(DjEngine engine, IEnumerable<GameInput> inputs, double end = 4, double frame = 0)
    {
        var ordered = inputs.OrderBy(i => i.Time).ToArray();
        int index = 0;
        if (frame > 0)
            for (double time = 0; time < end; time += frame)
            {
                while (index < ordered.Length && ordered[index].Time <= time)
                {
                    var input = ordered[index++]; engine.QueueInput(ref input);
                }
                engine.Update(time);
            }
        while (index < ordered.Length) { var input = ordered[index++]; engine.QueueInput(ref input); }
        engine.Update(end);
    }
    private static object Snapshot(DjEngine e) => new
    {
        e.EngineStats.TotalScore, e.EngineStats.NotesHit, e.EngineStats.Misses, e.EngineStats.Combo,
        e.EngineStats.MaxCombo, e.EngineStats.ScoreMultiplier, e.EngineStats.CoverageUnitsHit,
        e.EngineStats.EffectsUnitsHit, e.EngineStats.StarPowerPhrasesHit, e.EngineStats.StarPowerTickAmount,
        e.EngineStats.StarPowerActivationCount, e.EngineStats.TimeInStarPower, e.EngineStats.Stars,
        e.EngineStats.StarPowerScore, e.EngineStats.EffectsScore, e.EngineStats.TapsHit,
        e.EngineStats.ScratchesHit, e.EngineStats.CrossfadesHit, e.NoteIndex, e.CrossfaderPosition,
        Offsets = string.Join(",", e.EngineStats.GetOffsetSamples())
    };

    [TestCase(DjAction.Green, 0)]
    [TestCase(DjAction.Red, 1)]
    [TestCase(DjAction.Blue, 2)]
    public void TapPressHitsOnceReleaseAndHeldReportsDoNot(DjAction action, int code)
    {
        var e = Create($"384 = N {code} 0\n576 = N {code} 0");
        Run(e, new[] { Button(1, action), Button(1.5, action), Button(1.51, action, false) });
        Assert.That(e.EngineStats.TapsHit, Is.EqualTo(1));
        Assert.That(e.EngineStats.Misses, Is.EqualTo(1));
        Assert.That(e.EngineStats.TotalScore, Is.EqualTo(50));
    }

    [TestCase(.929, false)]
    [TestCase(.930, true)]
    [TestCase(1.070, true)]
    [TestCase(1.071, false)]
    public void TapWindowIncludesBothBoundaries(double time, bool hit)
    {
        var e = Create("384 = N 0 0");
        Run(e, new[] { Button(time, DjAction.Green) });
        Assert.That(e.EngineStats.NotesHit, Is.EqualTo(hit ? 1 : 0));
        Assert.That(e.EngineStats.Misses, Is.EqualTo(hit ? 0 : 1));
    }

    [Test]
    public void ChordComponentsAreIndependentAndMissBreaksComboOnce()
    {
        var e = Create("384 = N 0 0\n384 = N 1 0\n384 = N 2 0");
        int breaks = 0; e.OnComboReset += () => breaks++;
        Run(e, new[] { Button(1, DjAction.Green) });
        Assert.That(e.EngineStats.NotesHit, Is.EqualTo(1));
        Assert.That(e.EngineStats.Misses, Is.EqualTo(2));
        Assert.That(breaks, Is.EqualTo(1));
        Assert.That(e.EngineStats.Combo, Is.Zero);
    }

    [TestCase(4, 6, true)]
    [TestCase(4, -6, false)]
    [TestCase(8, -6, true)]
    [TestCase(8, 6, false)]
    [TestCase(12, -6, true)]
    [TestCase(12, 6, true)]
    [TestCase(12, 5.9f, false)]
    public void ScratchDirectionAndThreshold(int code, float velocity, bool hit)
    {
        var e = Create($"384 = N {code} 0");
        Run(e, new[] { Axis(1, DjAction.Platter, velocity), Button(1, DjAction.Green) });
        Assert.That(e.EngineStats.ScratchesHit, Is.EqualTo(hit ? 1 : 0));
    }

    [Test]
    public void ScratchRequiresHeldColorAndCannotFarmSteadySpin()
    {
        var e = Create("192 = N 12 0\n384 = N 12 0\n576 = N 12 0\n768 = N 12 0\n960 = N 12 0");
        Run(e, new[] { Axis(.5, DjAction.Platter, 10), Button(.8, DjAction.Green), Axis(1, DjAction.Platter, 11),
            Axis(1.4, DjAction.Platter, 3), Axis(1.5, DjAction.Platter, 6),
            Axis(2, DjAction.Platter, -6), Axis(2.5, DjAction.Platter, -7) });
        Assert.That(e.EngineStats.ScratchesHit, Is.EqualTo(2));
        Assert.That(e.EngineStats.Misses, Is.EqualTo(3));
    }

    [Test]
    public void FaderHysteresisAndEarlyHold()
    {
        var e = Create("384 = N 64 192\n768 = N 65 192");
        Run(e, new[] { Axis(.5, DjAction.Crossfader, -.6f), Axis(1, DjAction.Crossfader, -.4f), Axis(2, DjAction.Crossfader, -.3f) });
        Assert.That(e.EngineStats.CrossfadesHit, Is.EqualTo(2));
        Assert.That(e.EngineStats.CoverageUnitsHit, Is.EqualTo(8));
        Assert.That(e.CrossfaderPosition, Is.Zero);
    }

    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public void SpikeRequiresEntryAndReturn(bool returns, int hits)
    {
        var e = Create("384 = N 64 24");
        var inputs = new List<GameInput> { Axis(1, DjAction.Crossfader, -1) };
        if (returns) inputs.Add(Axis(1.0625, DjAction.Crossfader, 0));
        Run(e, inputs);
        Assert.That(e.EngineStats.SpikesHit, Is.EqualTo(hits));
        Assert.That(e.EngineStats.Misses, Is.EqualTo(1 - hits));
    }

    [Test]
    public void HoldingBeforeSpikeIsNotAnEntry()
    {
        var e = Create("384 = N 64 24");
        Run(e, new[] { Axis(.5, DjAction.Crossfader, -1), Axis(1.06, DjAction.Crossfader, 0) });
        Assert.That(e.EngineStats.SpikesHit, Is.Zero);
    }

    [Test]
    public void AdjacentSpikesHaveIndependentReturnDeadlines()
    {
        var e = Create("384 = N 64 24\n432 = N 66 24");
        Run(e, new[] { Axis(1, DjAction.Crossfader, -1), Axis(1.0625, DjAction.Crossfader, 0),
            Axis(1.125, DjAction.Crossfader, 1), Axis(1.1875, DjAction.Crossfader, 0) });
        Assert.That(e.EngineStats.SpikesHit, Is.EqualTo(2));
    }

    [TestCase(false, 0)]
    [TestCase(true, 4)]
    public void ScratchZoneUsesCoverageAndFixedBudget(bool move, int units)
    {
        var e = Create("384 = N 16 192");
        Run(e, new[] { Button(1, DjAction.Green), Axis(1, DjAction.Platter, move ? 10 : 0) });
        Assert.That(e.EngineStats.CoverageUnitsTotal, Is.EqualTo(4));
        Assert.That(e.EngineStats.CoverageUnitsHit, Is.EqualTo(units));
        Assert.That(e.EngineStats.TotalScore, Is.EqualTo(move ? 25 : 0));
    }

    [Test]
    public void ShortZoneDropoutIsForgivenButLongDropoutBreaksCoverage()
    {
        foreach (var gap in new[] { .03, .2 })
        {
            var e = Create("384 = N 16 192");
            Run(e, new[] { Button(1, DjAction.Green), Axis(1, DjAction.Platter, 8), Axis(1.1, DjAction.Platter, 0), Axis(1.1 + gap, DjAction.Platter, 8) });
            Assert.That(e.EngineStats.IsFullCombo, Is.EqualTo(gap < .05));
        }
    }

    [Test]
    public void EffectsWrapStartupAndBudget()
    {
        var e = Create("384 = S 35 192\n400 = N 97 0");
        var inputs = new List<GameInput> { Axis(.9, DjAction.EffectsDial, .98f) };
        for (int i = 0; i < 40; i++) inputs.Add(Axis(1.01 + i * .012, DjAction.EffectsDial, (i * .1f + .08f) % 1));
        Run(e, inputs);
        Assert.That(e.EngineStats.EffectsCuesHit, Is.EqualTo(1));
        Assert.That(e.EngineStats.EffectsUnitsHit, Is.EqualTo(4));
        Assert.That(e.EngineStats.EffectsScore, Is.EqualTo(25));
        Assert.That(e.EngineStats.TotalScore, Is.EqualTo(75));
        var initial = Create("384 = N 97 0");
        Run(initial, new[] { Axis(1, DjAction.EffectsDial, .8f) });
        Assert.That(initial.EngineStats.NotesHit, Is.Zero);
    }

    [Test]
    public void EffectsOnColoredLaneRequireItsButton()
    {
        var e = Create("384 = S 32 192");
        Run(e, new[] { Axis(.9, DjAction.EffectsDial, 0), Axis(1.1, DjAction.EffectsDial, .25f) });
        Assert.That(e.EngineStats.EffectsScore, Is.Zero);
    }

    private const string EuphoriaChart = "0 = S 2 192\n192 = S 2 192\n96 = N 0 0\n288 = N 1 0\n576 = N 64 192\n6144 = N 2 0";
    private static GameInput[] EuphoriaInputs => new[] { Button(.25, DjAction.Green), Button(.75, DjAction.Red), Button(1.1, DjAction.Euphoria) };

    [Test]
    public void TwoPhrasesActivateHalfBarDoubleScoreAndAutoFade()
    {
        var e = Create(EuphoriaChart);
        Run(e, EuphoriaInputs, 2.1);
        Assert.That(e.EngineStats.StarPowerPhrasesHit, Is.EqualTo(2));
        Assert.That(e.EngineStats.StarPowerActivationCount, Is.EqualTo(1));
        Assert.That(e.EngineStats.CrossfadesHit, Is.EqualTo(1));
        Assert.That(e.EngineStats.TotalScore, Is.EqualTo(250)); // two taps, doubled fade and hold
        Assert.That(e.EngineStats.IsStarPowerActive, Is.True);
        e.Update(9.2);
        Assert.That(e.EngineStats.IsStarPowerActive, Is.False);
        Assert.That(e.EngineStats.StarPowerTickAmount, Is.Zero);
        Assert.That(e.EngineStats.TimeInStarPower, Is.EqualTo(8).Within(.003));
    }

    [Test]
    public void MissedAndEmptyPhrasesDoNotAwardEuphoriaAndHeldButtonDoesNotAutoActivate()
    {
        var e = Create(EuphoriaChart + "\n768 = S 2 192");
        Run(e, new[] { Button(0, DjAction.Euphoria), Button(.25, DjAction.Green) }, 3);
        Assert.That(e.EngineStats.StarPowerPhrasesHit, Is.EqualTo(1));
        Assert.That(e.EngineStats.StarPowerActivationCount, Is.Zero);
    }

    [Test]
    public void EuphoriaPhraseWaitsForZoneCoverage()
    {
        var e = Create("384 = S 2 48\n384 = N 16 192");
        Run(e, new[] { Button(1, DjAction.Green), Axis(1, DjAction.Platter, 10) }, 1.2);
        Assert.That(e.EngineStats.StarPowerPhrasesHit, Is.Zero);
        e.Update(1.6);
        Assert.That(e.EngineStats.StarPowerPhrasesHit, Is.EqualTo(1));
    }

    [Test]
    public void ComboMultiplierStarsAndFreestyleExclusion()
    {
        string chart = string.Join("\n", Enumerable.Range(1, 32).Select(i => $"{i * 192} = N 0 0"));
        var e = Create(chart + "\n0 = N 20 384");
        Run(e, Enumerable.Range(1, 32).SelectMany(i => new[] { Button(i * .5, DjAction.Green), Button(i * .5 + .1, DjAction.Green, false) }), 17);
        Assert.That(e.EngineStats.TotalNotes, Is.EqualTo(32));
        Assert.That(e.EngineStats.Combo, Is.EqualTo(32));
        Assert.That(e.EngineStats.ScoreMultiplier, Is.EqualTo(4));
        Assert.That(e.EngineStats.TotalScore, Is.EqualTo(3400));
        Assert.That(e.EngineStats.Stars, Is.GreaterThan(3));
    }

    [TestCase(1.0 / 30)]
    [TestCase(1.0 / 60)]
    [TestCase(1.0 / 144)]
    [TestCase(.731)]
    public void MechanicsAreIndependentOfRenderFrameRate(double frame)
    {
        string chart = EuphoriaChart + "\n768 = N 16 192\n960 = S 35 192\n1000 = N 97 0\n1152 = N 66 24";
        var inputs = EuphoriaInputs.Concat(new[] { Axis(2, DjAction.Platter, 9), Axis(2.2, DjAction.Platter, 0),
            Axis(2.23, DjAction.Platter, 9), Axis(2.4, DjAction.EffectsDial, .98f), Axis(2.6, DjAction.EffectsDial, .1f),
            Axis(2.7, DjAction.EffectsDial, .2f), Axis(2.85, DjAction.EffectsDial, .3f), Axis(2.98, DjAction.EffectsDial, .4f) }).ToArray();
        var expected = Create(chart); Run(expected, inputs, 10);
        var actual = Create(chart); Run(actual, inputs, 10, frame);
        Assert.That(Snapshot(actual), Is.EqualTo(Snapshot(expected)));
    }

    [Test]
    public void SameTimestampOrderingAndFrameSplitsDoNotChangeScratchOrChordResults()
    {
        var inputs = new[] { Axis(1, DjAction.Platter, 9), Button(1, DjAction.Green), Button(1, DjAction.Red) };
        var expected = Create("384 = N 4 0\n384 = N 1 0"); Run(expected, inputs);
        var actual = Create("384 = N 4 0\n384 = N 1 0");
        foreach (var item in inputs.Reverse()) { var input = item; actual.QueueInput(ref input); actual.Update(1); }
        actual.Update(4);
        Assert.That(Snapshot(actual), Is.EqualTo(Snapshot(expected)));
        Assert.That(actual.EngineStats.NotesHit, Is.EqualTo(2));
    }

    [Test]
    public void SeekReplaysHistoryAndResetClearsPendingInputsAndAnalogHistory()
    {
        var e = Create(EuphoriaChart); Run(e, EuphoriaInputs, 3);
        object expected = Snapshot(e);
        e.Update(20);
        Assert.That(e.ProcessUpToTime(3, EuphoriaInputs), Is.EqualTo(3));
        Assert.That(Snapshot(e), Is.EqualTo(expected));
        var future = Button(16, DjAction.Blue); e.QueueInput(ref future);
        e.Reset(); Run(e, EuphoriaInputs, 3);
        Assert.That(Snapshot(e), Is.EqualTo(expected));
        e.Reset(); e.Update(20);
        Assert.That(e.EngineStats.TapsHit, Is.Zero);
        Assert.That(e.EngineStats.StarPowerActivationCount, Is.Zero);
        Assert.That(e.HeldButtons, Is.Zero);
    }

    [Test]
    public void PresetsCopyIndependentlyAndValidate()
    {
        var preset = new EnginePreset("DJ");
        var copy = (EnginePreset) preset.CopyWithNewName("Copy");
        copy.Dj.HitWindow.MaxWindow = .2;
        Assert.That(preset.Dj.HitWindow.MaxWindow, Is.EqualTo(.14));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnginePreset.DjPreset { ScratchRelease = 7 }.Create());
        Assert.Throws<ArgumentException>(() => Create("0 = S 2 384\n192 = S 2 384\n192 = N 0 0"));
        Assert.Throws<ArgumentException>(() => Create("0 = N 64 192\n0 = N 66 192"));
    }

    [Test]
    public void NormalizerSelectsDeckPreservesSignedByteScaleAndRejectsNonfinite()
    {
        var n = new DjInputNormalizer(DjDeck.Right, true, true);
        var values = n.Normalize(1, new(true, false, false, 0), new(false, true, true, -.5f), .75f, 1.25f, true).ToArray();
        Assert.That(values[0].Button, Is.False);
        Assert.That(values[1].Button, Is.True);
        Assert.That(values[3].Axis, Is.EqualTo(64));
        Assert.That(values[4].Axis, Is.EqualTo(-.75));
        Assert.That(values[5].Axis, Is.EqualTo(.25));
        Assert.That(DjInputNormalizer.SignedByteVelocity(1), Is.EqualTo(127));
        Assert.That(DjInputNormalizer.SignedByteVelocity(-1), Is.EqualTo(-128));
        Assert.Throws<ArgumentOutOfRangeException>(() => n.Normalize(1, default, default, float.NaN, 0, false).ToArray());
    }

    [Test]
    public void StatsCopyResetAndReplayBoundary()
    {
        var e = Create("384 = N 0 0"); Run(e, new[] { Button(1, DjAction.Green) });
        var copy = new DjStats(e.EngineStats);
        e.Reset();
        Assert.That(copy.TapsHit, Is.EqualTo(1));
        Assert.That(copy.TotalScore, Is.EqualTo(50));
        Assert.That(e.EngineStats.TapsHit, Is.Zero);
        Assert.Throws<NotSupportedException>(() => copy.ConstructReplayStats("DJ", false));
    }

    [Test]
    public void NullFadeIntervalSplitsCoverageEvenWithoutInputsAtItsBoundaries()
    {
        var e = Create("384 = N 64 384\n432 = S 96 288", new() { DropoutGrace = 0, CoverageRequired = 1 });
        Run(e, Array.Empty<GameInput>(), 2.1);
        Assert.That(e.EngineStats.CoverageUnitsHit, Is.EqualTo(6));
        Assert.That(e.EngineStats.CoverageUnitsTotal, Is.EqualTo(8));
    }

    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public void OuterLaneFadeGatingIsConfigurable(bool gate, int hits)
    {
        var e = Create("384 = N 0 0", new() { GateOuterLanes = gate });
        Run(e, new[] { Axis(1, DjAction.Crossfader, 1), Button(1, DjAction.Green) });
        Assert.That(e.EngineStats.TapsHit, Is.EqualTo(hits));
    }

    [Test]
    public void BlueAndOpenScratchesAndEffectsLeftHaveDistinctInputs()
    {
        var e = Create("384 = N 6 0\n576 = N 11 0\n768 = N 96 0");
        Run(e, new[] { Button(1, DjAction.Blue), Axis(1, DjAction.Platter, 7),
            Button(1.5, DjAction.Blue, false), Axis(1.5, DjAction.Platter, -7),
            Axis(1.8, DjAction.EffectsDial, .02f), Axis(2, DjAction.EffectsDial, .96f) });
        Assert.That(e.EngineStats.ScratchesHit, Is.EqualTo(2));
        Assert.That(e.EngineStats.EffectsCuesHit, Is.EqualTo(1));
    }

    [Test]
    public void SongSpeedScalesWindowAndTempoChangesKeepCoverageTickBudget()
    {
        var e = Create("384 = N 0 0"); e.SetSpeed(2);
        Run(e, new[] { Button(.88, DjAction.Green) });
        Assert.That(e.EngineStats.TapsHit, Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => e.SetSpeed(1));
        var tempo = Create("192 = N 16 384", sync: "0 = B 120000\n384 = B 60000");
        Run(tempo, new[] { Button(.5, DjAction.Green), Axis(.5, DjAction.Platter, 8) }, 2.1);
        Assert.That(tempo.EngineStats.CoverageUnitsHit, Is.EqualTo(8));
        Assert.That(tempo.EngineStats.TotalScore, Is.EqualTo(50));
    }

    [Test]
    public void ResetClearsSameTimeUncommittedReportAndDialStartup()
    {
        var e = Create("384 = N 0 0\n384 = N 97 0");
        var press = Button(1, DjAction.Green); e.QueueInput(ref press); e.Update(1);
        e.Reset();
        Run(e, new[] { Axis(1, DjAction.EffectsDial, .9f) });
        Assert.That(e.EngineStats.NotesHit, Is.Zero);
    }

    [Test]
    public void EuphoriaUsesMeasuresAcrossTempoAndTimeSignatureChanges()
    {
        var e = Create(EuphoriaChart, sync: "0 = B 120000\n768 = B 60000\n0 = TS 3\n768 = TS 4");
        Run(e, EuphoriaInputs, 2.1);
        // A half bar remains four measures; the change in meter/tempo alters its duration.
        Assert.That(e.EngineStats.IsStarPowerActive, Is.True);
        Assert.That(e.StarPowerEndTime, Is.GreaterThan(14));
        e.Update(e.StarPowerEndTime + .01);
        Assert.That(e.EngineStats.IsStarPowerActive, Is.False);
        Assert.That(e.EngineStats.StarPowerTickAmount, Is.Zero);
    }

    [Test]
    public void EuphoriaWaitsForSpikesLateReturnWindow()
    {
        var e = Create("384 = S 2 24\n384 = N 64 24");
        Run(e, new[] { Axis(1, DjAction.Crossfader, -1), Axis(1.12, DjAction.Crossfader, 0) }, 1.2);
        Assert.That(e.EngineStats.SpikesHit, Is.EqualTo(1));
        Assert.That(e.EngineStats.StarPowerPhrasesHit, Is.EqualTo(1));
    }

    [Test]
    public void DynamicDifficultyChangeIsExplicitlyUnsupported()
    {
        var e = Create("384 = N 0 0");
        YARG.Core.Engine.BaseEngine<DjNote, DjEngineParameters, DjStats> generic = e;
        Assert.Throws<NotSupportedException>(() => generic.ReplaceChart(new(Instrument.DjSingle, Difficulty.Hard)));
    }

    [Test]
    public void ShortZonesCannotEarnInitialDropoutGraceWithoutMovement()
    {
        var e = Create("384 = N 16 12");
        Run(e, Array.Empty<GameInput>());
        Assert.That(e.EngineStats.NotesHit, Is.Zero);
        Assert.That(e.EngineStats.CoverageUnitsHit, Is.Zero);
        Assert.That(e.EngineStats.TotalScore, Is.Zero);
    }

    [Test]
    public void PerfectShortChartUsesFcReferenceForStarsAndDoesNotMutatePlayerNotes()
    {
        var e = Create("384 = N 0 0");
        Assert.That(e.EngineStats.NotesHit, Is.Zero);
        Assert.That(e.BaseScore, Is.EqualTo(50));
        Run(e, new[] { Button(1, DjAction.Green) });
        Assert.That(e.EngineStats.Stars, Is.EqualTo(5));
    }

    [Test]
    public void InputAtEuphoriaExhaustionUsesOrdinaryMultiplier()
    {
        var e = Create(EuphoriaChart + "\n3504 = N 2 0");
        Run(e, new[] { Button(.25, DjAction.Green), Button(.75, DjAction.Red), Button(1.125, DjAction.Euphoria),
            Button(9.125, DjAction.Blue) }, 9.2);
        Assert.That(e.EngineStats.TotalScore, Is.EqualTo(300));
        Assert.That(e.EngineStats.IsStarPowerActive, Is.False);
    }
}
