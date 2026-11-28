"""Small deterministic checks of split and phase descriptors (unittest)."""
import unittest
import numpy as np
import pandas as pd
from train_ranker import TOLERANCES, TARGET_COLUMN, artist_group, phase_trace, phase_features, split_test
from rhythm_metrics import series_counts, timing_errors


class RankingTests(unittest.TestCase):
    def test_timing_and_sparse_series(self):
        beats, taps = np.array([0, 4800]), np.array([4800])
        self.assertEqual(series_counts(beats, taps, 4800, 48000), (1, 2, 1))
        np.testing.assert_array_equal(timing_errors(beats, taps, 4800), [0])
        # Choosing the individually closest pair first would lose one match.
        np.testing.assert_array_equal(timing_errors(np.array([0, 3000]), np.array([2500, 5500]), 3000),
                                      [2500, 2500])
        self.assertEqual(series_counts(np.array([0, 48000, 240000]), np.array([0, 240000]), 1440, 48000),
                         (2, 2, 2))
        self.assertEqual(TOLERANCES[TARGET_COLUMN], 70)

    def test_cumulative_split(self):
        train, dev, test = {"a": None}, {"b": None}, {"b": None, "c": None}
        with self.assertRaises(ValueError):
            split_test(train, dev, test, False)
        self.assertEqual(split_test(train, dev, test, True), ({"c": None}, ["b"]))
        with self.assertRaises(ValueError):
            split_test(train, train, test, True)

    def test_artist_group(self):
        self.assertEqual(artist_group({"Artist": " BEARWOLF "}), "bearwolf")
        self.assertEqual(artist_group({"Title": "bearwolf - Track"}), "bearwolf")
        self.assertEqual(artist_group({"Title": "Unknown"}), "<unknown>")

    def test_constant_zero_phase(self):
        frames = np.arange(301) * 480
        def data(bands):
            return pd.DataFrame({"frame": frames, **{str(k): np.zeros(len(frames)) for k in range(bands)}})
        flux, energy = data(5), data(3)
        trace = phase_trace(flux, energy)
        self.assertEqual(trace.shape, (301, 8))
        self.assertTrue(np.array_equal(phase_features(np.arange(5) * 24000, trace), np.zeros(16)))
        with self.assertRaises(ValueError):
            phase_trace(flux.iloc[1:], energy)


if __name__ == "__main__":
    unittest.main()
