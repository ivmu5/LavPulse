# Further cumulative audit — first batch

Baseline: green merged PR #110, master 1a5fc10. Three focused passes, not a completed 100-pass audit.

1. Selective band autocorrelation in phase/gap/tail recovery: evaluate only two or four relevant lags, preserving the original formula. Full tempo-window correlation remains unchanged. Add 100 fixed-seed numerical equivalence cases.
2. Cache and cancellation validation: reject null section/window entries; respect cancellation even on empty analysis or empty correlation ranges.
3. Explicit PCM contract: waveform analysis requires 48 kHz stereo PCM (as already produced by import). External hosts with other sample rates must resample first; this is a deliberate behavioral change, not a new decoder limitation. Add regression and document in both READMEs.

No blanket cache invalidation, threshold change, release or merge. Performance improvement must be measured; compilation and new checks depend on CI. Real-track validation and the remaining 97 review passes are not complete.
