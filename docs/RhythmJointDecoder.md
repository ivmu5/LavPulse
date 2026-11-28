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
