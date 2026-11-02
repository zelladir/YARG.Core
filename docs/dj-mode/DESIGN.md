# Single-turntable DJ mode: Phase 1 design

Phase 1b update (2026-11-02): the headless engine and native chart importer are implemented. See [PHASE1B.md](PHASE1B.md) for the actual engine rules, timing contract, validation and remaining limitations, and [XMK.md](XMK.md) for import coverage/provenance. Library discovery, Unity and DJ replay payloads remain deferred. The original Phase 1 design below is retained as historical rationale; its proposed rules are superseded by those implementation documents.

Status: experimental parser foundation, 2026-11-02. This is an independent fork design, not an upstream commitment or a claim to reproduce DJ Hero's scoring exactly. Development used AI assistance.

## Scope and implementation status

The first playable target is one physical turntable, in the DJ Hero 2 style: green/red/blue taps, directional and free-direction scratches, scratch zones, crossfades and spikes, effects, and euphoria. Freestyle gameplay, rewinds, battles, megamixes, and two-table play come later.

This change implements the chart representation and single-table `.chart` loading, including synthetic tests. It does **not** implement a playable engine, Unity player, library scanning, audio routing, or DJ replay payloads. The engine rules below are proposed starting parameters for the next increment. They need hardware traces and playtesting before they become stable defaults.

Baseline: YARG.Core `6c0923ee79770dfe5557dd1bdfb9c65f2ea578cf` and YARG `f5bfbe6996b4af35722d4204a93c6cc5c8f52a45`, both synced from upstream `master`. Core targets .NET Standard 2.1, with .NET 10 tests. YARG's editor is `6000.3.5f2` (`3fa8bc678cb0`). YARG's contribution guide requires future PRs to target `dev`; the Unity phase must account for that rather than assuming its current `master` checkout is the eventual integration base.

## Controls and input boundary

Use PlasticBand's `Turntable`, not raw XInput packets in gameplay code. Choose one physical deck explicitly in the profile; left/right mounting is a controller setting, not `DjDouble` gameplay. Keep color meaning fixed when changing mounting.

| Gameplay input | PlasticBand control | Interpretation |
| --- | --- | --- |
| Green/red/blue | `leftTableGreen/Red/Blue` or `rightTableGreen/Red/Blue` | Timestamped press/release edges; held state gates scratches |
| Platter | Corresponding `leftTableVelocity` or `rightTableVelocity` | Signed velocity, calibrated polarity and noise floor |
| Crossfade | `crossfader` | Continuous axis mapped to green/center/blue with hysteresis |
| Effects | `effectsDial` | Circular position in [0, 1); unwrap successive samples |
| Euphoria | `buttonNorth` | Activation edge, not a fourth colored lane |
| Menus/pause | D-pad, start/select, face buttons | Keep menu actions in the menu binding set |
| Euphoria feedback | `SetEuphoriaBlink` | Visual/haptic feedback only; never part of judging |

The inspected XInput implementation reads platter velocity from the low signed byte of the raw field. Its translated `IntAxis` uses -128..127, so a physical range around +/-64 need not reach normalized +/-1. Do not apply a generic joystick deadzone of 0.5 or normalize as a full signed 16-bit stick. Convert once at the input boundary: raw-equivalent units are approximately `axis * 127` for positive samples and `axis * 128` for negative samples. Verify that mapping against the installed package and captured values before shipping. The approximately +/-64 range is a working hardware assumption from the brief, not a measurement from this run.

The existing XInput matcher names turntable subtype 23. The expected HIDrogen-to-PlasticBand path on SteamOS still needs a device smoke test; no Steam Machine or physical controller was accessed during Phase 1.

## Chart representation and loading

`SongChart.Dj` is an `InstrumentTrack<DjNote>` for `Instrument.DjSingle`. It uses normal `InstrumentDifficulty<T>`, `Phrase`, `TextEvent`, parent/child chords, note-state reset, cloning, and previous/next links. `DjNoteType` retains the proposal's note numbers rather than reinterpreting them as guitar frets. `LaneNote` identifies the distinct event type; `Lane` gives the colored/open deck lane. `IsSpike` is derived once during parsing and preserved on clone.

The input path is the existing `ChartReader` section reader, a dedicated `DjChartReader`, `MoonSongLoader.LoadDjTrack`, then `SongChart`. A separate DJ intermediate dictionary avoids changing Moonscraper's four-difficulty indexing or collapsing Beginner into Easy. MIDI and other existing loaders do not invent DJ data. Both `SongChart.FromDotChart` and `.FromFile` support the extension.

Recognized section names are exactly `[BeginnerDJTurntable]`, `[EasyDJTurntable]`, `[MediumDJTurntable]`, `[HardDJTurntable]`, and `[ExpertDJTurntable]`. `DJDualTurntable`, ExpertPlus, and near-matching names are not interpreted as single-table charts.

| Proposal events | Representation |
| --- | --- |
| `N 0..3` | Colored taps and the optional open tap extension |
| `N 4..15` | Up/down/any-direction scratches, including red and open extensions |
| `N 16..19` | Scratch zones with exact lengths |
| `N 20..22`, `24..26`, `68..70` | Freestyle markers retained as data; gameplay deferred |
| `N 64..66` | Green/center/blue crossfade intervals |
| `N 67` | Toggle spike interpretation for crossfades at the same tick; no extra note |
| `N 96..97` | Optional left/right effects-dial cues |
| `S 2` | `PhraseType.StarPower`, presented as euphoria; end-exclusive note flags |
| `S 32..35` | Green/red/blue/all-lane effects phrases |
| `S 96` | Crossfade-null phrase; preserve it for the engine to force center |
| Local `E` | Retained verbatim after trimming surrounding quotes/whitespace, including `dj_effect` and `dj_sample` |

Crossfades of at most `resolution / 4` ticks are spikes, as specified by the proposal; the swap marker inverts that result. Modifier order within the source does not matter. Guitar sustain cutoffs and chord snapping are intentionally bypassed: a one-tick crossfade is meaningful. Times and interval ends use the shared tempo map, including intervals spanning tempo changes.

Notes are sorted stably by tick. Exact duplicate (tick, note type) entries keep the first; duplicate difficulty sections also keep the first. Duplicate swap markers act as one marker. Unknown single-table codes, second-table codes, negative/non-numeric/overflowed values, and overflowing interval ends are logged and skipped without losing adjacent valid events. Conflicting but defined same-tick events are retained; the engine phase needs chart-validation diagnostics for impossible combinations and overlapping euphoria phrases. Euphoria uses [start, end), flags all non-freestyle components on its first and last note ticks, and excludes freestyle markers.

DJ data participates in append, song bounds, and beatline completion. Generic instrument bounds were corrected to include long earlier zones, child-note tails, and overlapping phrase tails. `GetTotalNoteCount()` counts stored event components, including zones/freestyle markers; it is **not** the future scoring-object count.

The parser does not yet interpret `DJArtist`, `DJGreenStream`, `DJRedStream`, `DJBlueStream`, `DJSample<number>`, or the proposed song.ini tags. Global DJ event text is preserved by the existing generic event path, but rewind, battle and megamix semantics are not executed. Effects names and sample selection remain ordered text events; Phase 2 should resolve the most recent applicable event at or before each zone, with a defined default for absent/unknown effects.

The fast song scanner, `AvailableParts`/part metadata, song cache serialization, difficulty ratings, and canonical BTrack hashing are separate from full chart loading. They are unchanged. A DJ-only folder is therefore not yet discoverable/playable in the normal song browser. Add those surfaces and an explicit cache/version policy before exposing the mode in Unity. Do not publish DJ scores under another instrument's hash.

## Chart sources

Implement the [CC0 DJ Hero Customs proposal](https://github.com/TheNathannator/GuitarGame_ChartFormats/tree/main/docs/Proposed-Formats/DJ-Hero-Customs) first. The source repository was checked at `3d3640574821e1f7f0ec4be70627893aa88fdff6`; its `chart-Track.md` blob was `b6b4e20bf52a506348a837f7757bc6841428441a`. Existing local copies supplied the note tables. All checked-in fixtures are synthetic and contain no game assets.

Second, implement an explicit `.xmk`/FSGMUB importer with a conversion report, using the [format documentation](https://anydir.github.io/?a=formats/official/xmkandfsgmub) and [djhtools](https://github.com/shockdude/djhtools) as references. The latter's repository license was verified as MIT; no code was imported. The ANYDIR page is client-rendered and did not expose its detailed format body to the web reader, so no binary layout is asserted here. Preserve tempo, durations, crossfade semantics and effect selection; report unsupported events rather than silently degrading them. Add golden conversions from redistributable fixtures before claiming native-format support.

## Proposed engine rules

Follow `BaseEngine<DjNote, DjEngineParameters, DjStats>`, the timestamped `GameInput` queue, `HitWindowSettings`, reset/seek behavior, and preset conventions used by Guitar and Keys. Create DJ-specific stats and action IDs rather than reusing a Keys replay payload. The optional engine skeleton is deferred in this change: the clean boundary is a fully loaded, tested chart model, with no partially functional engine registered in the game.

### Tap and scratch judgment

Start with a fixed 140 ms total window, 70 ms early/late, matching the current default hit-window magnitude in Core. Keep it preset-controlled and scale through the existing song-speed/calibration machinery. Each press edge consumes at most one eligible tap of that color. Simultaneous components are independently judged; one wrong component must not erase a correctly judged one. After the late boundary an unresolved component misses once. Open taps require a separately specified release/all-buttons-up rule before enabling that optional extension in gameplay.

Detect discrete scratches from threshold crossings or direction reversals, with the required colored button held (no colored buttons for open scratches). Initial calibration candidates in raw-equivalent units: activate at |v| >= 6, rearm below |v| <= 3, and allow a direct sign reversal to rearm immediately. Treat values within the noise band as rest. Thresholds, polarity, and any debounce belong in the serialized engine parameters; tune against real wireless-controller traces. A stationary held button is not a scratch, and one long noisy spin must not mint arbitrary repeated hits.

Up/down compare calibrated velocity sign, while any-direction accepts either sign. For a scratch zone, track held-button plus moving-platter coverage on chart-time intervals, allowing an initial 50 ms dropout grace as a provisional value. Score at fixed tick subdivisions rather than per Unity frame or input report. Keep continuous movement state separate from discrete scratch edges so a zone can be sustained without repeatedly rearming.

### Crossfader

Initial normalized thresholds: enter green below -0.55, blue above +0.55; return to center on crossing -0.35/+0.35. Retain the current state between thresholds to suppress jitter. Validate the sign on the mounted controller. A regular crossfade requires the target state within the tap timing window and then holds that state across its interval. Initially center is the target before any authored fade. Moving early and holding is valid at a regular marker; the same rule must be explicit in replays.

Spikes require reaching the indicated target and returning to the surrounding regular-fade state; their short length is not a requirement to dwell for a full tap window. Use independently bounded entry/return windows, capped around adjacent spikes, and test rapid reversals. A crossfade-null phrase forces the logical chart state to center and suspends fade penalties for its interval. It must not physically recenter or reinterpret the user's binding.

Use half-open intervals and resolve logical fade state before judging same-tick colored events. Decide during playtesting whether a mismatched fade also invalidates the outer-lane tap/scratch; the initial proposal is to judge the fade separately and keep red available everywhere. Euphoria auto-fade should satisfy chart checks without moving the user's physical state.

### Effects and euphoria

Unwrap the dial with shortest circular delta in [-0.5, 0.5). Ignore startup position as movement, reject non-finite input, and reset the prior sample on seek/disconnect. Effects-zone bonus should depend on capped angular travel per beat (initial target 1/8 revolution per beat), not number of callbacks. Award at most one bonus unit per chart subdivision; spinning faster cannot farm unlimited score. Explicit left/right cues compare signed travel. Audio effect parameters can remain continuous even when scoring uses fixed subdivisions.

Use the existing beat-based star-power reservoir as the implementation pattern for euphoria: an entirely completed phrase adds a quarter bar, activation requires half a bar, full-bar duration starts at eight 4/4 measures, and active scoring doubles the current multiplier. Specify phrase overlap, zone-coverage completion, and seek/reset invariants in tests before enabling it. Proposed DJH2-style behavior auto-satisfies crossfades during euphoria, while taps/scratches still require input. Euphoria does not auto-award dial bonuses. These are design choices, not verified original-game constants.

## Proposed scoring and stars

Use 50 base points per discrete tap, directional/any scratch, crossfade entry or effects cue. Treat a simultaneous group as one combo step only after every required component succeeds, while keeping per-component note statistics. Raise the multiplier at 10/20/30 completed groups to 2x/3x/4x; euphoria doubles it to 8x. A miss resets combo once for that group. Starting values should remain configurable and unranked until tested.

Continuous zones and regular-fade holds receive a bounded tick-based budget, initially 25 base points per quarter-note beat. Effects zones have a separate capped bonus budget. Never count a zone both as arbitrary per-frame hits and as sustained ticks. Freestyle markers receive zero ranked points until a deterministic, capped rule exists.

Compute a DJ maximum baseline from charted discrete objects and authored continuous budgets, excluding euphoria; do not use the raw stored note count. Tune star thresholds against a small licensed chart corpus. A provisional 1-5 star curve at 10/20/35/55/75 percent of the maximum non-euphoria score is suitable for unranked playtests; gold-star policy remains open. Store the scoring-policy version with future replays and keep DJ leaderboards separate.

## Replay and compatibility boundary

Only reserved `GameMode.Dj = 20` and `Instrument.DjSingle = 50` are enabled. Existing numeric values/order remain unchanged; 51 stays reserved for `DjDouble`. DJ phrase values are appended. This does not itself create a DJ replay format.

Core currently advertises replay version 19 and engine version 6. This change leaves both unchanged and adds explicit DJ rejection on frame read/write; the writer rejects before writing its frame bytes. Existing instrument layouts are unchanged. No corpus of historical replay files was available for byte-for-byte replay verification; synthetic current-version guitar, six-fret and pro-keys frames round-trip byte for byte, and the full existing suite plus enum review provide additional regression evidence.

Before writing DJ replays, allocate stable DJ action IDs, define analog value quantization and input ordering, serialize thresholds/window/scoring parameters, add `DjStats`, implement the replay factories/analyzer, and bump/gate the format as needed so old clients reject unsupported payloads. Replay inputs should be normalized device-independent samples with calibration already applied; the engine's thresholds and interpretation version must still be recorded. Test deterministic results across frame rates, same-time button/platter ordering, pause/seek, reset, and reconnect.

## Unity and audio plan

Add a `DjPlayer` and track renderer using the existing gameplay-player lifecycle, pools, camera/highway presets, pause/practice interfaces, and results events. Render three colored lanes, scratch arrows/zones, a visible fade path/spikes, effects zones, and euphoria feedback. Use original geometry/art/audio. Distinguish scored cues from retained but unsupported freestyle markers in development builds.

Build bindings on [YARG #1669](https://github.com/YARC-Official/YARG/pull/1669), inspected open at head `2929dfd4fefbdea32f7b3d99e827f27da225a501` (updated 2026-10-31). Its reusable/runtime binding-set separation is the integration target. Add a `(ControllerFamily.Turntable, GameMode.Dj)` default set beside its menu set, with explicit deck selection, axis calibration, and keyboard simulation. Do not duplicate the old `BindingCollection` architecture. Recheck the PR and its companion Core work before rebasing; no part of that PR was merged in Phase 1.

YARG's current `BassStemMixer` combines streams and supports volume matrices; `BassStemChannel` exposes per-stem volume, seeking, whammy pitch shifting and reverb. In particular, its current volume setter uses a zero-duration `ChannelSlideAttribute`, so it does not already provide smooth DJ crossfades. Add a dedicated mix-gain layer that composes DJ gain with user volume/mute/reverb and smooths transitions without competing slides. Map green/red/blue stream metadata to separate stems; red stays centered, while the outer stems follow the fade. Choose and playtest the center gain law using actual pre-mixed stems. With a single mixed audio file, only gameplay/visual crossfading is possible; it cannot isolate the two source songs.

[BASS](https://www.un4seen.com/bass.html) provides effects and custom DSP, but the game's current API is not a complete implementation of the proposal's effects catalogue. Implement/test a small cross-platform set first (for example filter and delay), then add beat-synchronized roll/stutter using buffering and tempo-aware scheduling. Unknown effects should safely bypass audio processing while preserving the chart event. Confirm which BASS/BASS_FX functions and native libraries exist in Linux builds; don't assume every platform exposes identical effects. Platter judgment does not require reversing the song stream; audible scratch resampling is a later audio feature.

## Roadmap and acceptance gates

Estimates are engineering days for one developer, excluding hardware availability, review and art production.

| Phase | Work | Rough effort | Exit condition |
| --- | --- | --- | --- |
| 1 (this change) | Model, parser, design, synthetic tests | 2-4 days | Headless build and full tests pass; no playable-mode claim |
| 1b | DJ engine/parameters/stats/presets and input normalizer | 4-7 days | Tap miss/hit, held-button scratches, hysteresis, zones, euphoria and frame-rate determinism tests |
| 2 | Unity player/rendering, #1669 bindings, scan/cache metadata, basic stem routing | 7-12 days | Synthetic chart playable with keyboard and actual turntable; practice/reset and Linux smoke tests |
| 3 | Effects, final scoring/stars, replays and `.xmk` conversion | 5-10 days | Cross-platform audio checks, conversion fixtures, legacy/DJ replay round trips |
| 4 | Freestyle, rewind, dual turntable and battle/megamix features | 10-20+ days | Separate designs, licensing-safe fixtures and mode-specific tests |

Phase 2 needs an editor installation/license, a compatible checkout of the profile rework, a Core dependency update, original visual assets, licensed separate stems, and access to the owner's turntable when the other projects release the machine. An editor/game build and a controller test remain mandatory even though Core passes headless tests.

## Licensing and unresolved choices

Both repositories state LGPL-3.0-or-later; new code stays under the repository terms. The chart proposal is CC0. Existing Moonscraper notices remain intact. No Dj-Engine source or game assets were copied. If MIT converter code is later imported, preserve its notice. YARG's contribution guide forbids ripped assets, calls DJ a stretch goal, and requires PRs based on `dev`; no upstream contact or submission is part of this phase. Core has no `CONTRIBUTING.md` in this checkout; its README's contribution text was reviewed. Neither reviewed guide states an AI-disclosure requirement, so commits use the owner's `Assisted-by: AI` policy.

Unity and BASS have separate proprietary terms. [Unity Personal](https://unity.com/products/unity-personal) currently describes eligibility below $200,000 in revenue/funding over the prior 12 months; confirm the owner's applicable eligibility when installing. BASS's [licensing page](https://www.un4seen.com/bass.html#license) describes free non-commercial use and paid terms otherwise. No Unity activation, binary redistribution, or licensing purchase was performed here.

Open decisions: which deck/mounting is default; measured platter noise/polarity and event cadence; original-game fidelity versus configurable YARG rules; whether outer-lane hits require a matching fade; exact effects and star curves; handling impossible/overlapping charts; and a redistributable chart/stem corpus. The owner need not resolve these to review the parser, but they should be settled before ranked gameplay.
