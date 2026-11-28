# Rhythm decoder modernization

Audit baseline: 2026-11-28, schema 8, source 214f6b5. Private audio and taps must never be committed. The new decoder runs only during diagnostic export.

## Plan and acceptance

1. Reproduce the 16 kHz audit frontend and role-aware adaptive baseline. Version the native PCM clock and time frontend/path separately. Check identical feature input against Python; the FIR is not FFmpeg.
2. Joint tempo/phase search: bounded 16-state beam, multiple related local tempo seeds, circular phase error, continuous phase reconstruction and a distinct alternative. No calibrated probabilities or inferred meter.
3. Review clocks and boundaries: chunk invariance, short tracks, end padding, cancellation, anti-phase, finite data. Separate observed attack from predicted grid. Gate digital silence.
4. Review evaluation: exact one-to-one matching within annotated runs, strict 50/70/100/180 ms, phase-randomized null, macro/micro scores. Compare identical feature streams. Add controls for tempo changes, offbeats, gaps and aliases. Ten tracks are development data, not a held-out benchmark.
5. Review cost: bounded feature count/beam, separate timing, reusable buffers. Profile decoder/FFT/key/queues on ARM64. Cache key must include audio hash, decoder, feature version and clock; no persistent cache yet.
6. Extend role templates: kick/snare alternation, triple subdivisions, explicit pause/continuation/reset state and ambiguity. Add independently labeled downbeats.
7. Validate >=60 independent clips, split by composition/artist. Audit proposed gates: macro F1 at 70 ms +.05 with positive paired interval; p95 8-beat phase forecast <=50 ms; scan p95 <=1.10x and PSS +<=10 MB on ARM64, zero additional underruns. Only then consider opt-in promotion and rollback.

## Limits

C# baseline is audit-inspired, not a bit-identical port. Joint path is a bounded approximation, not a full probabilistic metrical model. No tap calibration or training in either decoder. Observed flag means low-band support, not a verified beat. No persistent feature cache or playback change. Strict metrics use a fixed 2 s gap rule. Hardware tests and native compilation are required.

Schema 9 adds audit-flux.csv and baseline/joint/alternative state and beat CSVs. Earlier schemas still replay old paths. Keep original reports locally.

## Five review passes on the initial implementation

1. Scope safety, trace reuse, feature-clock and circular boundary checks; avoid materializing every alternative grid.
2. Comparable strict annotated-series metrics and 32 phase-shift controls for all paths.
3. Reusable zero-padded FFT autocorrelation; direct reference test checks signed linear ACF at multiple lengths.
4. Activity-gap boundaries and separate low/high attack evidence. Tested shorter-scale tempo blending and phase reset independently; neither materially improved the ten-track set, so the more expensive blend and forced reset were not adopted.
5. Assembly informational version, joint parameters, detailed producer-side wall timings, cancellation and phase-null tests. Inline key and queue wait are distinguished; worker CPU time and device profiling are still missing. Timings are wall-clock, not additive CPU measurements.

Independent Python/FFmpeg replay on the ten development tracks (aligned to exported PCM clock): mean F1 at 70/100/180 ms was 0.333/0.480/0.814 for the audit two-pass model and 0.311/0.452/0.805 for the first joint model. All nine supplied synthetic controls retained baseline agreement or improved it (kick/snare alternation 0.667 -> 1.000 at 70 ms); half-time-only control remained ambiguous. The joint path is experimental, not a replacement. These are not measured C# results. The 6/16 s blend and forced-reset variants did not produce a meaningful improvement. Keep these negative results rather than tuning per track.

Next: compare schema 9 export against Python on the exact same 100 Hz input; measure clock and C# performance; extend alternating timbre templates and metrical ambiguity; only promote after independent annotated corpus and ARM64 checks. Do not treat manual bass taps as a complete beat/downbeat reference.
