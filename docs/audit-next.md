# Further cumulative audit — first batch

Baseline: green merged PR #110, master 1a5fc10. Ten focused passes, not a completed 100-pass audit. Passes 4–10 build on the first commit.

1. Selective band autocorrelation in phase/gap/tail recovery: evaluate only two or four relevant lags, preserving the original formula. Full tempo-window correlation remains unchanged. Add 100 fixed-seed numerical equivalence cases.
2. Cache and cancellation validation: reject null section/window entries; respect cancellation even on empty analysis or empty correlation ranges.
3. Explicit PCM contract: waveform analysis requires 48 kHz stereo PCM (as already produced by import). External hosts with other sample rates must resample first; this is a deliberate behavioral change, not a new decoder limitation. Add regression and document in both READMEs.

No blanket cache invalidation, threshold change, release or merge. Performance improvement must be measured; compilation and new checks depend on CI. Real-track validation and the remaining 90 review passes are not complete.

## Second batch (passes 4–10)

4. Onset extraction: eliminate two per-hop float arrays; recompute band differences only at candidate peaks. Validate finite nonnegative input energies.
5. Band flux: reuse previous square-root band values instead of calculating each neighbor twice.
6. Frame validation: end-exclusive beats, transient and diagnostic positions, matching energy envelope length. Invalid cached grids will be rejected rather than read with out-of-range markers.
7. Consistent half-open tempo-window boundaries shared by primary and uncovered-section recovery, including odd stride.
8. Recovery: replace predicted frames with an order-independent binary lookup; avoid an intermediate GetRange copy.
9. Separate RhythmTempoPath, precompute identical transition penalties, preserve tie order; 50 fixed-seed reference comparisons.
10. Phase prefix: reject mismatched input lengths and respect cancellation for empty inputs.

The tests are source additions, not successful executions until CI reports them. The changes do not claim improved real-track accuracy or measured acceleration. No analysis schema change.
