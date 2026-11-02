# DJ as a band instrument

Phase 1c, 2026-11-02. This is the current product direction and supersedes the standalone-first scope in [DESIGN.md](DESIGN.md). The turntable accompanies ordinary MIDI band songs. Native DJ mashups remain supported by [XMK.md](XMK.md). This fork has a headless engine, generator, sidecar loader and library metadata; Unity gameplay and audible mixing are still a separate integration phase.

## Source and mapping policy, generator 1.0.0

`DjBandGenerator.LoadSource()` calls Core's MIDI loader with sidecar merging disabled. It honors `five_lane_drums`, `hopo_frequency`, `sustain_cutoff_threshold` and the supported `multiplier_note = 103` override. Other overdrive values use the normal MIDI 116 default. It does not read audio or infer timing from waveforms. `GenerateFolder()` hashes `notes.mid` before loading and checks it again after generation. Neither MIDI nor INI is written.

Use the highest nonempty Expert/Hard/Medium/Easy source difficulty on each instrument. Use Core's pro-drums interpretation (including cymbals, toms and its elite-drums fallback), then four/five-lane drums if necessary. Do not stack four-lane and pro-drums copies. Five-fret guitar, co-op, rhythm, bass, keys, pro keys, lead vocals (harmonies if lead is absent), sections, global coda, and source phrases contribute. Guitar chords contribute one rhythmic onset, not one onset per fret. This release does not use pro guitar/bass or native six-fret-only tracks as additional sources.

| Source | DJ cue |
| --- | --- |
| Kick and snare; bass and rhythm guitar onsets | Green taps |
| Other drum/tom/hi-hat/ride hits; guitar/co-op, keys and pro-key onsets | Blue taps |
| Crash/explicit drum accents, authored section starts and vocal phrase starts | Red taps, at least 750 ms apart |
| Three consecutive non-kick drum or guitar onsets at sixteenths or faster | The interior onsets become alternating up/down green/blue scratches |
| Drum fills | Green scratch zones; overlapping fills are unioned |
| Section names containing verse | Left/beat hold |
| Section names containing solo or bridge | Right/melodic hold |
| Chorus and other section names | Center hold |
| Isolated red accents on Expert | Occasional opposite-side spike, at least eight beats apart |
| Solos and sections containing breakdown | All-lane effects zones |
| Overdrive phrases | Euphoria (`S 2`) |
| BRE/coda | Freestyle crossfade marker through the chart end |

The repetition detector is deliberately rhythmic: it does not identify a melody or require identical fret shapes. All tempo positions use MIDI ticks. Expert onsets round to the nearest sixteenth (ties up), bounded by the source end. Duplicate tick/lane candidates collapse. A scratch zone takes precedence over scratches, which take precedence over taps. Green precedes red and blue when a chord needs reduction. Zones exclude all other hand cues through their end plus the tier's minimum gap. Kick-only runs remain taps. Red is an accent rather than a continuously busy third lane.

Section names are matched case-insensitively; names from Core's section translation still contain recognizable verse/chorus/solo/bridge text. Missing sections are flagged using `SongChart.HasAuthoredSections`, before Core synthesizes its percentage-based practice sections. Generated practice sections do not manufacture musical section transitions. Sections are copied to the sidecar, with quote/bracket/newline characters sanitized for `.chart` syntax.

A regular fade lasts to the next source section, or coda/chart end. If the opposite outer lane has a zone or consecutive onsets less than one beat apart in that interval, choose center. Spikes require 300 ms clearance from neighboring hand cues and 750 ms from regular fade entries; the engine suspends the surrounding hold during the spike and requires return to the preceding hold. Effects start one beat into their source interval and are cut around fade entries/spikes (half a beat before through one beat after), so the free hand is not asked to move both controls together. Overlapping euphoria/effects intervals are unioned. Coda clips normal play and retained zones; freestyle is preserved as data, with no new freestyle scoring claim.

Core can derive drum activation phrases when they were not explicitly authored. The generator uses the loaded chart, including those Core-derived fills. A full player/audio test is still needed to judge whether generated musical choices are enjoyable. These are documented, deterministic heuristics, not an audio transcription or a measured confidence model.

## Difficulty reduction and validation

Create Expert, then thin Hard from Expert, Medium from Hard, Easy from Medium, and Beginner from Easy. Lower tiers retain only onsets aligned to their coarser grid; they do not move notes to new times. Convert both discrete scratches and zone starts to single taps at Medium and below. Drop excess chord components and notes too close in real time, including across tempo changes. This nested process guarantees nonincreasing component counts. Equal counts on very sparse charts are permitted.

| Tier | Grid | Minimum gap between hand groups | Maximum simultaneous hand cues | Maximum discrete scratch rate | Fade policy |
| --- | --- | --- | --- | --- | --- |
| Expert | 1/16 | 75 ms | 2 taps, or 1 scratch/zone | 8/s | Holds and isolated spikes, at least 0.5 s apart |
| Hard | 1/16 | 120 ms | 2 taps, or 1 scratch/zone | 5/s | Every other retained hold, no spikes, at least 1 s apart |
| Medium | 1/8 | 180 ms | 2 taps | None | Every other Hard hold, at least 2 s apart |
| Easy | 1/8 | 250 ms | 1 tap | None | None |
| Beginner | 1/4 | 400 ms | 1 tap | None | None |

Effects appear only on Hard/Expert. Euphoria remains on all tiers where the phrase contains a playable cue. Scratch rates are capped after thinning; a scratch that would exceed its cap becomes a tap. No open-lane cues, opposing simultaneous scratches, taps during zones, duplicate lanes, or three-button chords are generated. The fader and button/platter can be used simultaneously with separate hands.

`DjBandGenerator.Validate()` checks a loaded DJ track independently for monotonic counts, hand-group gaps, zone overlap, lane legality, scratch rate, fade rate, and dial/fade entry conflicts. It runs on every generated chart after serialization and merge. Component density counts taps, scratches, zone starts and fader cues, excluding unscored freestyle and phrase records. It is not a count of continuous scoring subdivisions or a physical skill rating.

## Quality and visibility

Start at 100; subtract each applicable penalty once, clamp to 0..100. Hide scores below **60** by default. A review UI can expose the hidden part without hiding the entire band song.

| Flag | Bit | Penalty / trigger |
| --- | --- | --- |
| MissingSections | 1 | -15; no authored source sections |
| SparseSource | 2 | -30; neither drums nor bass/rhythm onsets |
| IrregularTempo | 4 | -10; BPM outside 45..240, adjacent tempo ratio changes over 25%, or odd numerator/non-quarter denominator |
| RubatoTempo | 8 | -15; tempo-event count exceeds max(8, duration seconds / 4) |
| ExtremeDensity | 16 | -15; raw source candidates exceed 14/s, before merging/thinning |
| SyncMismatch | 32 | -100 when generated output fails sync validation; reject external sidecar on load |
| ConstraintViolation | 64 | -100 if loaded generated output violates playability limits |
| SourceChanged | 128 | Reject generated sidecar whose source SHA-256 differs |
| InvalidSidecar | 256 | Reject unreadable, oversized or malformed metadata |
| NoPlayableNotes | 512 | -100 for empty generation; reject a sidecar without playable DJ cues |

RubatoTempo means a tempo map needs review; finely authored live-band timing can trigger it even when perfectly synchronized. Odd time is not itself a defective chart. Quality is an explanation of the heuristic's risk, not a measured perceptual rating. CLI input/IO failures are separate `InputError` report rows and a nonzero exit status. Low score alone does not reject parsing; it marks the part hidden. Rejected sidecars leave the original band tracks available and carry diagnostics in `SongChart.DjSidecar` / library metadata.

## `dj.chart` version 1

A conventional UTF-8 (no BOM), LF `.chart`, in this order:

1. `[Song]`, with the original MIDI resolution and `Offset = 0`.
2. `[SyncTrack]`, sorted tempo (`B`, integer millibpm) and time-signature (`TS numerator log2-denominator`) events from the loaded MIDI map.
3. `[Events]`, authored sections as `E "section ..."`.
4. `[BeginnerDJTurntable]`, `[EasyDJTurntable]`, `[MediumDJTurntable]`, `[HardDJTurntable]`, `[ExpertDJTurntable]`, exactly the CC0 proposal's single-table names.

Generator header fields are additional `[Song]` properties, ignored by older generic chart readers:

```text
DjGenerator = "YARG.djgen"
DjGeneratorVersion = "1.0.0"
DjSourceSha256 = "<64 lowercase hex characters>"
DjQuality = 85
DjFlags = 16
DjFlagNames = "ExtremeDensity"
DjHidden = false
DjBeatStems = "bass.ogg;drums.ogg;rhythm.ogg"
DjMelodicStems = "guitar.ogg;keys.ogg;vocals.ogg"
DjBackingStems = "song.ogg"
```

Only the exact `DjGenerator = "YARG.djgen"` marker in `[Song]` grants generator ownership. No marker, another generator, or an ambiguous duplicate property means **do not overwrite**. Hand-authored sidecars can use any subset of the five tiers and have quality -1 (unrated). Easy never implies a separately authored Beginner part. Removing the generator marker is the way to preserve manual edits. Keep versioned backups externally; this release does not infer whether somebody edited a file while retaining its ownership marker.

The source hash covers MIDI bytes, not audio, folder names or INI. `[Song] Offset` is zero because Core MIDI tick time starts at zero. Existing `song.ini` delay remains the enclosing song's audio offset and is applied once by Unity, equally to every instrument. Repeating delay in the sidecar would desynchronize DJ; nonzero offsets are rejected.

Standard `.chart` stores only three decimals of BPM. Write nearest millibpm, then compare tick positions and BPM within 0.000501 BPM, plus exact resolution and time signatures. Explicit malformed/duplicate sync records and unsupported anchor records are rejected rather than relying on the generic parser's permissive recovery. After a successful check, **retime all DJ notes/children, phrases and text events using the original MIDI map**. The sidecar's rounding never replaces band timing or accumulates drift. Generated source-hash mismatches are rejected even if the tempo happens to agree.

Default deck lists are sorted audio filenames. Recognized formats match the INI audio loader (.ogg, .opus, .mp3, .wav, .aiff). `stems.txt`, if present, is an inventory for the local corpus; otherwise enumerate the folder's audio filenames. They are routing hints, not proof that audio exists, nor authority to override current human ownership. Metadata and chart output contain no timestamps, random IDs, absolute source paths or runtime timings. Same inputs, settings, generator version and stem inventory produce byte-identical output independent of job count and culture.

Writes use a same-directory temporary file and atomic replacement, with ownership checked again before replacing an existing file. An existing hand-authored destination is preserved and reported. Optional sidecars are limited to 16 MiB; DJ events more than sixteen 4/4 measures past the source end are rejected before beatline expansion. Do not run an editor and generator concurrently on the same owned sidecar; this is not a multi-process editing transaction protocol.

## Loading and library cache

`SongChart.FromFile()` merges a sibling `dj.chart` for `.mid`/`.midi`; pass `mergeDjSidecar: false` for generation. `SongChart.FromMidi()` has no filesystem context and remains in-memory only. `DjSidecar.Merge()` and `MergeFile()` are explicit APIs for those callers. The ordinary unpacked INI song-entry load path also merges the sidecar; it does not replace or append any non-DJ track.

The unpacked scanner performs a full validated chart load **only when a sidecar exists**. `AvailableParts.Dj` holds the exact available difficulty mask and a provisional 0..6 intensity, ceil(max tier component count / duration / 1.5), capped at 6. `SongEntry.DjQuality`, `DjFlags`, and `DjHidden` expose review metadata. `HasInstrument(DjSingle)` and `HasDjPart()` hide flagged low-quality parts by default. `HasDjPart(true)` and `entry[DjSingle]` expose actual presence for review. Difficulty-mask queries describe the actual stored part; use the visibility query when presenting choices.

Cache schema is bumped to **26_11_02_01** because `AvailableParts` is stored as raw bytes and unpacked entries now include a sidecar fingerprint and review metadata. Full scans compare sidecar SHA-256, so addition, deletion and changes with an unchanged timestamp invalidate the entry. Quick scans intentionally trust the cache, as they do for other song files; request a full scan after generation or review. Old cache files must rebuild. Existing band hashes still refer to their original MIDI. `GetLeaderboardHash(DjSingle, ...)` explicitly throws until a versioned DJ hash/replay format is implemented; no fallback to a guitar/band score identity is allowed.

This phase covers unpacked `notes.mid`/`notes.midi` band folders. It does not add archived SNG/CON sidecars, `.chart`-only band generation, normal library discovery for standalone XMK files, or canonical DJ score submission. The generator's required batch input is `notes.mid` only.

## Audio APIs and exact modes

`DjAudioMode`, `DjStemGroup`, and `DjAudioRouting` are Core APIs; they do not call the audio backend. `ClassifyFile()` describes inventories; `Classify(SongStem)` describes mixer channels. Unity passes the **actual resolved human-owned channels**, aggregated across all human players, to `IsControlled()` / `Gain()`. `HumanClaims(instruments)` supplies nominal claims, including all drum sub-stems, shared vocal stems and rhythm for co-op guitar. Union in any fallback/shared channels used by the mixer. A single mixed `song` channel containing a human's instrument must be protected or the session must use another audio mode.

| Group | Stem names / channels |
| --- | --- |
| Left: beat | drums, drums_1..4; bass; rhythm |
| Right: melodic | guitar; keys; vocals, vocals_1..2 |
| Center: backing | song / backing, normalized to SongStem.Song |
| Excluded | crowd, preview, SFX, metronome, venue samples, unknown names |

Clean/explicit vocal variants belong to the same vocal group. The helper recognizes `backing` as a routing alias; Unity's loader must resolve that alias to `SongStem.Song` when using nonstandard filenames. It must never infer channel isolation from a filename alone.

Let `f` be clamped to [-1,1], left=-1, center=0, right=+1. Gains are multiplicative DJ gains, composed with existing user volume, instrument mute, speed and other audio controls.

**Unclaimed stems (default band audio choice):** remove every human-claimed channel. Vocals are excluded unless `allowVocals` is enabled, and still must be unclaimed. DJ owns the remaining beat, melodic and backing channels. Beat gain is `1-max(0,f)`; melodic gain is `1+min(0,f)`; backing gain is 1. Center preserves the authored mix level. At each extreme the opposite deck is silent. This unity-center law avoids boosting already balanced stems. A missed/unrecovered DJ state sets all DJ-owned channel gains to zero, including backing; the normal instrument recovery event restores them. Effects route only to these owned channels. Human-owned gains and DSP are never changed by DJ in this mode. If no channels remain, gameplay still works and the audio layer has nothing to change.

**Whole-mix color:** ignore instrument ownership for this explicit mix-wide setting. Beat gain is `10^(-f*depthDb/20)`, melodic (including vocals) is `10^(f*depthDb/20)`, backing remains unity for the tilt. Default depth is 6 dB; helper accepts 0..24 dB. At +1, beat is -6 dB and melodic +6 dB; reverse at -1. Misses do not mute or change gain. Effects may color all music channels, including backing and claimed instruments, but exclude crowd/SFX/metronome. Unity must provide headroom or a limiter for the boost. A single mixed channel cannot be spectrally separated by this helper; use a gentle whole-mix effect or unity tilt as an explicit fallback.

**Gameplay only:** every DJ gain is 1, `IsControlled()` is false, and effects are bypassed; drawing, judging, euphoria, score and chart selection are unchanged. Misses never affect audio.

Unity owns roughly 10-20 ms gain smoothing, allocation-free per-frame routing snapshots, effect DSP and bypass, pause/seek/disconnect reset, and recovery callbacks. Recompute claims atomically on player join/leave or instrument change; release stale DJ gains/effects before assigning a channel to a human. Never leave a muted/filtered channel behind. Only one DJ should own the shared mix control per session; multiple DJ profiles can score with gameplay-only audio until arbitration is designed. An audio mode is not a scored engine rule and should not change note points.

## Rules and Unity integration

`EnginePreset.DjPreset.Band()` is the default in newly constructed `EnginePreset`: configurable Phase 1b thresholds/points, outer-lane fade gating off and euphoria auto-fade off. `StandaloneDjHero2()` retains the Phase 1b standalone values, including auto-fade on. These are separate factories, not shared mutable singleton objects. The standalone policy is DJH2-inspired; exact original-game scoring and hardware constants have not been verified. Existing DJ replay/serialization rejection remains in place.

The Unity checkout remains unchanged. Target editor from its `ProjectVersion.txt`: **6000.3.5f2, revision 3fa8bc678cb0**. Integration work:

1. Deliberately update the Core dependency and recheck the profile/bindings rework before integrating. Register `DjSingle` as another player in ordinary band sessions, with one three-lane highway, platter/scratch and fade cues, effects/euphoria feedback, keyboard simulation, deck/polarity calibration and disconnect reset. Keep native XMK entry points available separately.
2. Add DJ to song browser instrument filters and setlists using the visibility query. Display generated/unrated/review badges, quality and flag explanations, available tiers and intensity. Offer an explicit show-hidden toggle. Preserve all ordinary band entries; a missing or rejected DJ part should trigger a player-specific difficulty/instrument choice when a setlist item starts. Run a full rescan after generation/review.
3. Store the three-way audio setting per profile, with unclaimed stems as the proposed default, an allow-unclaimed-vocals toggle and whole-mix depth slider defaulting to 6 dB. Apply routing at song setup and ownership changes. Wire gain multiplication and DSP in BASS without competing volume slides. Test mixed/fallback audio, all instruments claimed, shared vocals, mute recovery, effects bypass and joining/leaving.
4. Show DJ note mechanics, coverage, euphoria, score, combo and stars on the band results screen alongside other players. Show generation quality separately from performance. Do not upload/rank DJ scores or record DJ replays until the existing explicit replay/hash boundary is resolved. Setlist persistence must retain the DJ instrument/difficulty choice and review visibility choice.
5. Test with keyboard, real turntable traces, actual multistem playback, Linux editor/player builds, practice seeks, full band sessions and SteamOS when the coordinator releases that machine. Headless tests do not establish audible synchronization, stem balance or physical playability.

Unity and BASS retain their separate license/redistribution requirements described in the design; verify applicable terms when installing/building the game. This phase installs neither and imports no proprietary assets. New source uses the repository's LGPL-3.0-or-later terms. The chart proposal is CC0; no Dj-Engine code is used and the existing MIT notices remain intact.

## CLI, publishing and validation

```sh
dotnet run --project DjGen -- /songs --report /tmp/dj-report.csv --jobs 4
dotnet run --project DjGen -- /songs --write --report /tmp/dj-report.csv --jobs 4
dotnet run --project DjGen -- /songs --only /songs/artist/song --report /tmp/one.csv
dotnet run --project DjGen -- /local-corpus --write --output /separate/scratch --report /tmp/corpus.csv
dotnet publish DjGen/DjGen.csproj -c Release -r linux-x64 --self-contained true -o /output/linux-x64
dotnet publish DjGen/DjGen.csproj -c Release -r win-x64 --self-contained true -o /output/win-x64
```

Dry run is the default. Multiple roots deduplicate songs. `--only` accepts absolute or current-directory-relative folder/subtree filters and consumes following folder arguments until the next option. `--jobs` accepts 1..64, default min(CPU count,8). Recursive scanning skips reparse points/symlinks; explicitly pass a symlink target when required. `--output` writes numbered folders outside all input roots, with the source-to-output mapping in CSV. `--write` is still required. It is designed for the local validation corpus; normal library generation omits `--output`.

CSV rows are sorted by input path and include source hash, destination, status, quality, hidden, flags, duration, elapsed generation milliseconds, counts and notes/sec for each tier, and errors. Only elapsed timings are nondeterministic. Existing hand-authored outputs report `protected-hand-authored`. Exit 0 means all matched inputs were processed (including protected/flagged songs); 1 means per-song errors; 2 means invocation/enumeration failure. No matches or unmatched `--only` filters fail explicitly. Copy the **entire** self-contained publish folder, not just the executable.

Synthetic MIDI tests cover mapping, all tiers, tempos/offset, sync rejection, source hashes, determinism/culture, ownership protection, cache addition/edit/deletion, independent Beginner, visibility, audio ownership and presets. The opt-in `DjBandTests.LocalBandCorpus` uses `YARG_DJ_BAND_CORPUS`; no real charts or excerpts belong in this repository. Local corpus metrics, three generated excerpts, exact validation commands and publish paths are in the coordinator's external `REPORT-1c.md`.
