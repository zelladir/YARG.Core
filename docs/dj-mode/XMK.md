# XMK / FSGMUB import

Status: independent, original C# implementation, 2026-11-02. Development used AI assistance. No Dj-Engine source was consulted or copied during Phase 1b. No real chart or audio bytes are checked in.

## Sources and layout

The implementation was written from format facts in these references, read at djhtools commit `baa889dfd9ac17f2a5765e43a437044fe44a3beb`:

- [djh_fsgmub_csv_convert](https://github.com/shockdude/djhtools/blob/baa889dfd9ac17f2a5765e43a437044fe44a3beb/djh_fsgmub_csv_convert/djh_fsgmub_csv_convert.py): binary layout, string offsets, typed extra data and CRC scope.
- [djh_fsgmub_xmk_convert.py](https://github.com/shockdude/djhtools/blob/baa889dfd9ac17f2a5765e43a437044fe44a3beb/misc/djh_fsgmub_xmk_convert.py): dialect differences and explicit DJH2 spike IDs.
- [djh_fsgmub_chart_convert](https://github.com/shockdude/djhtools/blob/baa889dfd9ac17f2a5765e43a437044fe44a3beb/misc/djh_fsgmub_chart_convert/djh_fsgmub_chart_convert.py): four quarter-note beats per native measure and event meanings.
- [ANYDIR XMK/FSGMUB article source](https://anydir.github.io/md/formats/official/xmkandfsgmub.md): cross-check of event IDs, beat-length timing and nominal BPM metadata. The article body was fetched directly, beyond the client-rendered shell seen in Phase 1.

djhtools is MIT; its full [notice is retained](licenses/djhtools-MIT.txt) alongside this provenance even though converter implementation code was not ported. New C# code is under this repository's LGPL-3.0-or-later terms. The references are not chart/audio redistribution grants.

The header is four big-endian 32-bit words: version, CRC32, event count, string-blob length. Versions 1 and 2 are accepted. Each 16-byte event is a big-endian float measure position, uint type, float measure length, and four-byte extra field. Native measures are converted to four quarter-note beats at 1920 PPQ by default, rounding to the nearest tick away from zero. Event times and both interval endpoints use the resulting tempo map.

The extra field is a string offset relative to the first event for author/section/markup events, integer microseconds per beat for beat-length events, and float BPM for nominal BPM events. Strings must point into the declared string blob and be terminated valid UTF-8. CRC32 covers byte 8 through the end of declared string data; alignment padding is excluded. Bad CRC is a report diagnostic by default and can be made fatal. Structural corruption always fails.

## API and mapping

`XmkImporter.Import(bytes, options)` returns `XmkImportResult`: `Chart`, `Version`, `ChecksumValid`, authors, diagnostics and raw unsupported events. `FromFile` is a convenience API. It infers DJH1 for `.fsgmub`, DJH2 otherwise, and recognizes `DJ_Beginner/Easy/Medium/Hard/Expert` filenames. Explicit options override inference. Header version is not used to guess the game dialect. The default difficulty for an unrecognized name is Expert.

`SongChart.FromFile` routes `.xmk`/`.fsgmub` through the importer and logs its diagnostics. Use the importer directly when you need the conversion report, a tempo supplied by metadata, or an explicit dialect. This phase does not merge a folder's difficulty files or discover DJ-only songs in the library.

| Native events | Core representation |
| --- | --- |
| 0/1/2 | Green/blue/red taps; native order differs from the proposal's green/red/blue order |
| 3-8 | Green/blue up, down and any-direction scratches |
| 9/10/11 | Blue/center/green regular fades |
| 27/28/29 | Explicit green/blue/center spikes in DJH2; short regular fades remain regular |
| 20/21 | Green/blue scratch zones |
| 12/13/14/22 | Green/blue/red/all-lane effects phrases |
| 15 | Euphoria phrase with end-exclusive note flags |
| 23 | Crossfade-null phrase |
| 16/17/31/32 | Freestyle/sample markers preserved; sample selection retained as `dj_sample <extra>` |
| 0x05FFFFFF through 0x06000009 | Ordered `dj_effect <name>` text, including documented unused-filter alias |
| Author/section/markup | Result authors, `SongChart.Sections`, and track text respectively |
| Beat length / nominal BPM | Tempo map; beat length takes precedence when both occur at one tick |
| Chart begin | Accepted structural marker without a scoring object |
| Other events | Original position/type/length/extra retained in `UnsupportedEvents`, with diagnostics |

DJH1 option maps 48-51 to zones/all-lane effects/null fade. Its short fades use the proposal's <=sixteenth-note spike heuristic, not a claim to reproduce every contextual DJH1 spike case. DJH2 spikes are explicit, with no duration heuristic. Taps/scratches retain their raw converted duration; the Phase 1b engine uses separate scratch zones for coverage.

Events are sorted stably. Duplicate `(tick, note type)` pairs keep the first and report the duplicate. Same-tick components become normal parent/child chords with previous/next links. Euphoria flags exclude freestyle components. Effect selection is preserved as ordered text; audio processing is not implemented.

Beat-length changes supply timing. A nominal BPM event without a beat-length event at that tick is used with a diagnostic. If there is no tempo at tick zero, the caller must supply `FallbackBpm`; there is no silent 120 BPM default. This is particularly relevant to DJH1 charts whose tempo lives in external metadata.

## Validation and limits

The parser rejects unknown header versions, truncated tables/blobs, invalid UTF-8, invalid/outside/unterminated string pointers, nonfinite or negative event times, conflicting beat durations, and invalid tempos. Resource limits are 32 MiB, 250,000 events, 10,000 measures, 48-9600 PPQ, and 1-1000 BPM. These are importer limits, not native-format claims.

Twenty ordinary unit cases use only bytes produced by a synthetic helper. They exercise the mappings, chord/link/flag behavior, two tempo sources, variable tempo/durations, DJH1 options, inferred difficulty, text/effect/sample metadata, unknown events, CRC/padding, malformed structures, pointers, encoding and resource bounds.

An explicit `XmkImporterTests.LocalCorpus` test reads only the directory named by `YARG_DJ_TEST_CORPUS`. It is excluded from ordinary tests and no fixture contents are committed. The development corpus is a sibling of the repository at `/data/projects/yarg-dj/test-corpus-local/`; `.git/info/exclude` also excludes nested directories of that name.

The three local DJH2 files present during this run imported with valid CRCs and zero diagnostics/unsupported events. Component/group/phrase counts were 425/299/8, 200/171/14, and 316/233/13. Each had one tempo event. Headless bot validation had zero supported-object misses; the third file contains three unscored freestyle components. These are local structural/gameplay smoke checks, not evidence of exact original-game behavior or a redistributable golden corpus.

Still out of scope: native format export; battle, megamix, rewind and lyric semantics; exact DJH1 hold/spike fidelity; audio; automatic external metadata ingestion; song-library scanning/cache/available-parts support; and DJ replays.
