# Phase 1b: headless DJ engine

Status: experimental, unranked policy 1, 2026-11-02. This document supersedes the proposed engine rules in the Phase 1 design where the two differ. Development used AI assistance.

## Delivered boundary

`DjEngine : BaseEngine<DjNote, DjEngineParameters, DjStats>` runs the single-turntable chart model without Unity. It uses the existing timestamped `GameInput` queue, `HitWindowSettings`, preset copying, stats inheritance, callbacks, speed scaling, reset, and `ProcessUpToTime` history replay. DJ-specific scheduling replaces guitar sustain/lane behavior. `.xmk` import is documented in [XMK.md](XMK.md).

`EnginePreset.Dj` holds provisional gameplay and mounting settings. Left deck, ordinary platter polarity and ordinary fader polarity are defaults, not hardware findings. `DjInputNormalizer` selects a deck without swapping colors and emits green/red/blue buttons, platter, crossfader, effects position and euphoria. It takes PlasticBand's normalized platter axis and converts positive samples by 127 and negative samples by 128. A raw velocity of roughly 64 is therefore usable without reaching normalized 1. Raw engine platter actions use signed-byte-equivalent units; do not normalize a second time.

The normalizer is stateless. Reset the engine on disconnect to clear held buttons, spin state and dial startup history. Nonfinite device reports are rejected before yielding their first action. Axes are clamped/wrapped, and polarity is applied at this boundary.

## Timing contract

Queue inputs in chronological order using Core's `QueueInput`; its existing behavior clamps out-of-order timestamps. All controls from a device report must have the same timestamp. Different controls at the same timestamp are applied atomically, in a fixed semantic order. The last value for a repeated control at that timestamp wins. A timestamp remains open until `Update` advances **strictly past** it. Calling `Update(t)` after only part of a report will not prematurely judge it; call a later update to observe finalized outcomes.

`CurrentTime` is the requested update time. `JudgmentTime` exposes the logical input/chart time during callbacks, which is useful when a large update processes several events. This deliberate one-timestamp deferral is part of policy 1 and must be preserved in any future replay implementation.

Continuous scoring integrates the intervals between input changes and authored boundaries, never render frames. Units end at quarter-beat subdivisions by default, with a final partial unit. Each interval's integer budget is the difference between successive cumulative floors, so partial units cannot increase the total budget. Coverage is measured in chart seconds within each subdivision. Tempo changes change durations without changing the authored point budget.

`Reset()` clears pending and queued inputs, notes, stats, dial history, scratch state and euphoria. `Reset(true)` retains colored buttons and the fader position but clears motion/dial history. `ProcessUpToTime(time, inputs)` rebuilds by replaying input history; it does not pretend a seek can reconstruct missing inputs. Change speed after reset, before updates. Dynamic difficulty replacement explicitly throws: construct a new engine and replay history. The generic `ReplaceChart` method was made virtual solely to let DJ reject that unsupported path; other instruments keep their existing behavior.

## Policy 1 mechanics

All listed numeric settings are provisional and configurable through `DjPreset`/`DjEngineParameters`.

| Mechanic | Implemented rule |
| --- | --- |
| Taps | Green/red/blue press edges, inclusive fixed +/-70 ms window. A press consumes at most one matching tap. Repeated held reports and releases do not tap. |
| Scratch edges | Activate at magnitude 6, rearm at magnitude <=3, or activate a direct reversal. Up is positive and down negative after calibration. Any-direction accepts either. Required color must be held; optional open scratches require no color held. One edge can satisfy different lanes in a scratch chord, once per lane. |
| Scratch zones | Held color plus magnitude >=6. Default 80% coverage per subdivision; a dropout gets at most 50 ms grace after actual movement has occurred. An unplayed short zone cannot gain free initial grace. Every subdivision must pass for the zone object to succeed. |
| Fader | Enter outer positions at +/-0.55 and release toward center at +/-0.35. A regular fade may be reached early and held. Its entry and hold budget are separate; missing entry does not erase subsequently earned coverage. |
| Spikes | A transition must enter the target in the entry window and return to the preceding regular target. Default preceding target is center. Return opens at max(start, end -70 ms), closes at end +70 ms, capped at the next fade/spike start. Holding a target from before the window is not a spike. |
| Fade holds | The authored target must be held; spike intervals temporarily suspend the surrounding hold check. Null-fade phrases suspend fade checks and outer-lane gating, without changing the physical fader. |
| Outer-lane gating | Off by default. When enabled, center accepts either outer lane; an outer fader position accepts its color. Red remains available. |
| Effects cues | Shortest circular dial delta in [-0.5, 0.5); first sample is initialization. Left/right cues use the delta sign. No bonus for a stationary dial. |
| Effects phrases | Capped travel budget of 1/8 revolution per beat, 25 base points per beat. At most one award per subdivision. Colored phrases require that button; all-lane phrases do not. Travel at a subdivision endpoint contributes to the interval ending there; phrase end is exclusive. Faster movement cannot create additional units. |
| Euphoria | A fully completed nonempty phrase gives 1/4 bar. Phrase membership is end-exclusive and waits for zone coverage and spike return windows. Activation is a press edge requiring 1/2 bar; holding before becoming ready does not activate later. Full capacity lasts eight measures; tempo and meter use Core's measure conversion. Active scoring doubles the multiplier and, by default, auto-satisfies fades. It does not supply taps, scratches or effects. Exhaustion is end-exclusive. |
| Combo | A simultaneous group advances once after all required components succeed. A failed component/coverage resets that group's combo once. Later zone completion can occur after subsequent discrete groups, by design. Unmatched inputs do not impose an overhit penalty in policy 1. |
| Score | 50 base points per discrete tap/scratch/fade/effects cue; 25 per beat for scratch zones and regular fade holds; separate capped effects budget. No discrete 50-point bonus for zone completion. Multiplier becomes 2/3/4 at 10/20/30 completed groups; active euphoria doubles it. |
| Stars | 1-5 thresholds at 10/20/35/55/75% of a deterministic, non-euphoria autoplay reference, including combo ramp, continuous budgets and effects. No gold-star/ranked policy. Reference simulation uses a chart clone and does not alter player notes. |

Freestyle markers are retained by parsers but excluded from scoring and note totals. Optional open taps explicitly throw until their input semantics are designed. Conflicting same-tick fades, overlapping euphoria phrases, and overlapping effects budgets explicitly fail validation instead of creating ambiguous scoring. Other chart-authoring validation, including all impossible simultaneous scratch combinations, remains future work.

Native tap and scratch durations survive import. Policy 1 judges taps and discrete scratches as discrete objects and uses separate authored scratch zones for continuous coverage; it does not implement native tap-hold gameplay or exact original-game scoring.

## Integration and limits

The engine is headless and unranked. `DjStats` includes per-mechanic hits, finalized misses, coverage/effects units, combo, score, stars and Core euphoria fields. Its full-combo predicate includes coverage. Stats snapshots copy independently and reset retains chart totals. DJ parameter/stat serialization and replay-stat construction explicitly throw; existing replay version 19, engine version 6 and existing instrument layouts are unchanged. The Phase 1 DJ frame read/write rejection remains in force.

Bot mode is available for validation and the score reference. It supplies successful mechanics and continuous units but does not activate euphoria. This is not evidence of controller playability or exact DJ Hero 2 fidelity. The straightforward timeline implementation has not been benchmarked against arbitrary maximum-size charts; the scheduler caps event expansion at two million events.

Normal library discovery remains deferred: available parts, fast scanner, cache serialization/versioning, difficulty metadata, DJ `song.ini`/`song.json`, canonical score hashing and audio-stem metadata still need a coordinated change. Cache layout/version are unchanged. `SongChart.FromFile` loading an `.xmk` does not imply song-browser support.

Unity remains outside this phase: use the previously verified editor 6000.3.5f2, integrate with the profile/turntable binding architecture, add player/track rendering and keyboard simulation, route licensed separate stems, and validate hardware/SteamOS when the owner makes it available. No Unity editor, audio playback or physical controller test ran here.

## Validation

All ordinary engine tests use synthetic `.chart` strings and timestamped traces. They cover taps and timing boundaries, independent chord misses, directional/any/open scratches, small signed-byte values, rearm and reversals, fader hysteresis, spike entry/return and adjacency, zone grace/coverage, effects wrap/budget, euphoria capacity/activation/expiry and meter changes, combo/stars, copies, reset, seek and same-time input order.

The same mixed trace is compared at 30, 60 and 144 Hz, irregular 0.731-second updates, and one large update. Equality includes scores, stats, offsets, combo, stars and euphoria duration. This verifies frame-rate independence for those traces, not invariance to arbitrarily changed controller sampling or precision across every possible runtime.
