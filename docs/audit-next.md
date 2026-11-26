# Further cumulative audit — first batch

Baseline: green merged PR #110, master 1a5fc10. Thirty focused passes, not a completed 100-pass audit. Passes 4–30 build on preceding commits.

1. Selective band autocorrelation in phase/gap/tail recovery: evaluate only two or four relevant lags, preserving the original formula. Full tempo-window correlation remains unchanged. Add 100 fixed-seed numerical equivalence cases.
2. Cache and cancellation validation: reject null section/window entries; respect cancellation even on empty analysis or empty correlation ranges.
3. Explicit PCM contract: waveform analysis requires 48 kHz stereo PCM (as already produced by import). External hosts with other sample rates must resample first; this is a deliberate behavioral change, not a new decoder limitation. Add regression and document in both READMEs.

No blanket cache invalidation, threshold change, release or merge. Performance improvement must be measured; compilation and new checks depend on CI. Real-track validation and the remaining 70 review passes are not complete.

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

## Fifth batch (passes 25–30)

25. Centralize decoder/analysis normalization in PcmNormalization, retaining SIMD and scalar behavior, including exactly aliased spans.
26. Acquire decoder ownership before reading optional metadata; dispose on constructor failure, including property getter exceptions.
27. Observe cancellation after the recording loop before reusing cached analysis; skip empty PCM writes.
28. Clamp initial deck cursor position like Seek and reject negative length.
29. Validate EQ input frame count and prevent nonfinite input from contaminating active recursive filters or their transition.
30. Validate transport fade frame count before modifying fade state.

Targeted regressions cover constructor cleanup, cursor bounds and malformed PCM counts. Fixed explicit switch-expression precedence in the prior batch's test. No new playback settings or cache version change. CI for the latest head is pending.

## Sixth batch (passes 31–34)

31. Observe cancellation before allocating band evidence, during flux prefixes and phase choices, including empty input and early-return cases. Reject invalid hop rates; empty input no longer creates a phantom bin.
32. Deduplicate overlapping integer correlation lags in MeasurePair, preserving band accumulation order and interpolation. Add comparisons with the full correlation for identical, nearby and separated tempos.
33. Reject invalid meter section tempos and bounds before inferring bar accents; add cancellation checks during long continuity and phase scans. Add NaN-tempo regression.
34. Reduce tempo compatibility to one logarithm using octave symmetry; preserve the three previous hypotheses to floating-point precision. Add symmetric half/double-tempo checks.

Focused source audit, not an exhaustive repository-wide pass for each number. No cache version, settings, merge or release. New checks require CI; no measured speedup or real-track accuracy claim.

## Seventh batch (passes 35–40)

Resume the original thematic roadmap. Passes 31–34 included rhythm/profile work rather than exclusively concurrency; keep their numbers and real scope. Remaining roadmap: 41–50 memory/GC/allocations/SIMD; 51–60 cache/files/library; 61–70 UI/FPS; 71–80 native platforms; 81–90 robustness/stress; 91–100 architecture, refactoring and documentation.

35. Validate decoder concurrency limits and reject unmatched releases without corrupting the slot counters. Keep the reserved preparation lane.
36. Track semaphore ownership across cancellation after acquisition, releasing the acquired permit and reference exactly once.
37. Own asynchronous scheduler queue snapshots instead of borrowing mutable caller arrays.
38. Make producer-owned parallel analysis cleanup idempotent; reject submissions after disposal and skip empty transfers. Concurrent Submit/Dispose is still unsupported.
39. Acquire the playback-run guard before allocating the mixer, validate output/cancellation, and clear run ownership even if initialization fails.
40. Publish the visual delay and spectrum settings under the same startup lock to eliminate lost setting updates during output creation.

Regression checks cover slot cancellation/accounting, mutable scheduler arrays, empty and disposed analysis workers, cancelled/failed playback and visual delay across restart. Static source review only; CI and native-device checks pending. No cache version or settings changes. No merge or release.
