# Further cumulative audit — first batch

Baseline: green merged PR #110, master 1a5fc10. Twenty-four focused passes, not a completed 100-pass audit. Passes 4–24 build on preceding commits.

1. Selective band autocorrelation in phase/gap/tail recovery: evaluate only two or four relevant lags, preserving the original formula. Full tempo-window correlation remains unchanged. Add 100 fixed-seed numerical equivalence cases.
2. Cache and cancellation validation: reject null section/window entries; respect cancellation even on empty analysis or empty correlation ranges.
3. Explicit PCM contract: waveform analysis requires 48 kHz stereo PCM (as already produced by import). External hosts with other sample rates must resample first; this is a deliberate behavioral change, not a new decoder limitation. Add regression and document in both READMEs.

No blanket cache invalidation, threshold change, release or merge. Performance improvement must be measured; compilation and new checks depend on CI. Real-track validation and the remaining 76 review passes are not complete.

## Second batch (passes 4–10)

4. Onset extraction: eliminate two per-hop float arrays; recompute band differences only at candidate peaks. Validate finite nonnegative input energies.
5. Band flux: reuse previous square-root band values instead of calculating each neighbor twice.
6. Frame validation: end-exclusive beats, transient and diagnostic positions, matching energy envelope length. Invalid cached grids will be rejected rather than read with out-of-range markers.
7. Consistent half-open tempo-window boundaries shared by primary and uncovered-section recovery, including odd stride.
8. Recovery: replace predicted frames with an order-independent binary lookup; avoid an intermediate GetRange copy.
9. Separate RhythmTempoPath, precompute identical transition penalties, preserve tie order; 50 fixed-seed reference comparisons.
10. Phase prefix: reject mismatched input lengths and respect cancellation for empty inputs.

The tests are source additions, not successful executions until CI reports them. The changes do not claim improved real-track accuracy or measured acceleration. No analysis schema change.

## Third batch (passes 11–16)

11. Numeric Camelot identity, avoiding formatting and parsing strings in compatibility comparisons.
12. Compatibility excludes invalid BPM and nonfinite/out-of-range energy from evidence.
13. Tonal PCM rejects partial stereo frames, checks early cancellation, reserves a final unknown interval when the bounded key timeline fills.
14. Discard powers below the existing absolute LUFS gate before storing them; retain all short-term display points. Same gating formula. Verify block partition invariance.
15. Weight waveform energy/bass by represented PCM frames, including incomplete final display bins. Derived EnergyVersion 2 triggers regeneration from saved waveform (and dependent structure), retaining valid meter/key/loudness without PCM decode.
16. Validate supplied cached profile before reusing its versions. Malformed profiles rebuild from available waveform/rhythm.

All new checks await CI. No measured performance or real-track accuracy claim. No global cache format change. The bounded local key timeline reserves one additional unknown interval; already cached key timelines are unchanged.

## Fourth batch (passes 17–24)

17. Allocate exact downbeat arrays without LINQ iterators; check cancellation during long accent scans.
18. Emit only one structure record per contiguous run instead of allocating a replacement record for each energy point. Preserve accumulation order.
19. Maintain sums of absolute-gated powers during LUFS streaming rather than traversing the stored powers again.
20. Normalize public custom PCM before all analysis filters, with SIMD and scalar tail. Nonfinite samples become zero; finite values outside [-1,1] are clamped. Ordinary imported PCM is already normalized.
21. SIMD normalization of native-rate stereo decoder blocks, preserving nonfinite-to-zero and clipping behavior.
22. Empty resampler reads do not consume input; explicit disposed guard and idempotent decoder disposal.
23. Validate final RIFF chunk padding and WAVE_FORMAT_EXTENSIBLE valid bit count/extension size. Malformed files previously tolerated may now be rejected.
24. Validate PCM16 decode destination before writing. New regression cases for odd PCM8 RIFF, partial buffers, invalid samples, resampler end and disposal.

No global cache reset or new analysis thresholds. CI and actual acceleration remain unverified for this batch.
