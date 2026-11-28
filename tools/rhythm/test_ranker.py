"""Small deterministic checks of split and phase descriptors (unittest)."""
import unittest
import numpy as np
import pandas as pd
from train_ranker import artist_group, phase_trace, phase_features, split_test


class RankingTests(unittest.TestCase):
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
