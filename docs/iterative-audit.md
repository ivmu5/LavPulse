# Iterative audit / Итерационный аудит

Baseline: PR #109 commit 9ef5596 (all five platform/playback checks passed at inspection). The audit uses the preceding edited source as its next input. The ten passes below are **focused review passes**, not ten independent exhaustive proofs of every source file. The refactor follows the last pass.

| Pass | Scope | Changes |
|---:|---|---|
| 1 | Rhythm evidence | Reuse normalized band pulse field; cancellation during autocorrelation. |
| 2 | Tempo priors | Require measured quarter-rate evidence before adding a beat-over-bar bonus; test unsupported quarter. |
| 3 | Grid recovery | Continue supported tail probes inside the previous boundary; binary search replacement ranges. |
| 4 | Tonal analysis | Cache FFT frequency bounds; stack-based 12-bin normalization. |
| 5 | Meter | Aggregate phase groups in one traversal per candidate. |
| 6 | PCM reading | Enforce block and rate limits at the reader; regression for oversized reads. |
| 7 | Retention | Protect retained PCM paths shared by aliases; defer pending aliases; deduplicate deletion, isolate cleanup failures. |
| 8 | Async lifetime | Register work before shutdown snapshot; link diagnostics and thumbnails; limit diagnostic decode slots and make close idempotent. |
| 9 | Queue scheduling | Isolate compact-analysis and PCM failures; bounded loops and a regression for mode changes. |
| 10 | UI / cleanup | Avoid redundant layout writes; retain visibility callbacks; release unused successors when the queue is cleared with a deck still present. |

Final refactor: model types, section tracking, gap/phase recovery, onset helpers, profile analyzer, mix compatibility and loudness estimator are separate files. Namespaces and public API signatures remain unchanged. The two README languages describe the user workflow as well as developer/release information.

The cache remains analysis-v13. New ranking and recovery decisions apply on fresh analysis or explicit rescan. No global cache reset or forced migration is introduced. No new thresholds are advertised as accuracy improvements until tested on real tracks. Precomputed pulse evidence adds one double per ~20 ms flux bin; for a four-minute track this is about 96 KB. No hard-real-time guarantee, verified meter/chord detector, true peak, semantic song segmentation, harmonic auto-selection or full asynchronous PCM read-ahead has been added.

Validation: compare the exact new PR head against CI; baseline success does not validate the new code. New targeted console assertions cover the unsupported quarter-rate prior, cancellation, oversized PCM reads, invalid rate and mode-isolated failure. No local .NET SDK was available in the audit environment. Physical device testing, full real-track replay, profiling and a full repository-wide exhaustive review remain necessary. No original audio/diagnostic archives are committed.
