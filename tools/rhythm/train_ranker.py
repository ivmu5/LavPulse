"""Offline experimental ranking of existing LavPulse rhythm candidates.

No audio, annotations or fitted parameters are committed. Only the training
reports contribute target values to fitting. Evaluation reports are disjoint
by SHA-256 ID. Not used by the player.
"""
import argparse
import json
import re
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd
from scipy.ndimage import maximum_filter1d, uniform_filter1d
from sklearn.linear_model import Ridge
from sklearn.preprocessing import StandardScaler

from rhythm_metrics import series_counts

RATE = 48000
CONTROLS = ("audit-baseline", "envelope-decay", "envelope-hybrid",
            "envelope-rise", "joint", "legacy", "envelope-refined-decay")
TOLERANCES = (30, 50, 70, 100, 180)
TARGET_COLUMN = TOLERANCES.index(70)
FEATURES = ("relative_log_period", "abs_relative_log_period", "log_bpm_120",
            "duration_coverage", "interval_mad", "long_gap_fraction",
            "event_coverage", *("event_alignment_" + s for s in
            ("strength", "kick", "snare", "hat", "tail")),
            *("beat_support_" + s for s in ("strength", "kick", "snare", "hat", "tail")),
            "beat_support_unweighted")
BANDS = ("flux_30_100", "flux_100_220", "flux_220_600", "flux_600_2000",
         "flux_2000_7900", "rise_low", "rise_mid", "rise_high")
PHASE_FEATURES = tuple("onbeat_" + b for b in BANDS) + tuple("onbeat_minus_half_" + b for b in BANDS)


def f1(beats, taps, ms):
    """Chronological one-to-one matching inside annotated runs only."""
    matched, predicted, count = series_counts(beats, taps, ms * RATE // 1000, RATE)
    return 2 * matched / (predicted + count) if count else 0.0


def nearest(source, query):
    i = np.searchsorted(source, query)
    left = np.clip(i - 1, 0, len(source) - 1)
    right = np.clip(i, 0, len(source) - 1)
    a, b = np.abs(source[left] - query), np.abs(source[right] - query)
    return np.minimum(a, b), np.where(a < b, left, right)


def features(beats, events, strengths, total, base_period):
    """Audio-only candidate descriptors; no user marks or candidate name."""
    gaps = np.diff(beats)
    period = np.median(gaps)
    relative = np.log2(period / base_period)
    error, _ = nearest(beats, events)
    coverage = (events >= beats[0]) & (events <= beats[-1])
    alignment = np.exp(-.5 * (error / (period * .12)) ** 2) * coverage
    error, indices = nearest(events, beats)
    support = np.exp(-.5 * (error / (period * .12)) ** 2)
    return np.array([relative, abs(relative), np.log2(60 * RATE / period / 120),
                     len(beats) * period / total, np.median(np.abs(gaps - period)) / period,
                     np.mean(gaps > 1.5 * period), coverage.mean(),
                     *((alignment[:, None] * strengths).sum(0) /
                       np.maximum(1e-9, strengths.sum(0))),
                     *((support[:, None] * strengths[indices]).mean(0)), support.mean()])


def strictly_ordered(values):
    return len(values) > 0 and np.all(np.diff(values) > 0)


def phase_trace(flux, energy):
    """Build locally normalized onset evidence on the common 10 ms clock."""
    expected = np.arange(len(flux), dtype=np.int64) * 480
    if len(flux) < 3 or not np.array_equal(flux.frame.to_numpy(), expected) or not np.array_equal(energy.frame.to_numpy(), expected):
        raise ValueError("Phase experiment requires complete aligned 10 ms traces.")
    raw = flux.iloc[:, 1:].to_numpy(dtype=float)
    envelope = energy.iloc[:, 1:].to_numpy(dtype=float)
    if raw.shape[1] != 5 or envelope.shape[1] != 3 or not np.isfinite(raw).all() or not np.isfinite(envelope).all() or np.any(raw < 0) or np.any(envelope < 0):
        raise ValueError("Invalid spectral or envelope evidence.")
    def normalize(x):
        return x / (x + np.maximum(uniform_filter1d(x, size=201, axis=0, mode="nearest"), 0) + 1e-6)
    envelope = normalize(envelope)
    rise = normalize(np.maximum(0, envelope - np.vstack([np.repeat(envelope[:1], 3, axis=0), envelope[:-3]])))
    return maximum_filter1d(np.c_[normalize(raw), rise], size=5, axis=0, mode="nearest")


def phase_features(beats, trace):
    """Sample near regular beat intervals, then contrast with their midpoints."""
    gaps = np.diff(beats)
    period = np.median(gaps)
    valid = (gaps >= .75 * period) & (gaps <= 1.25 * period)
    starts, gaps = beats[:-1][valid], gaps[valid]
    if not len(starts):
        return np.zeros(len(PHASE_FEATURES))
    frames = np.arange(len(trace)) * 480
    on, half = [np.array([np.interp(starts + q * gaps, frames, trace[:, k]).mean()
                         for k in range(trace.shape[1])]) for q in (0, .5)]
    return np.r_[on, on - half]


def artist_group(annotation):
    # Unknown artists share one conservative group rather than splitting
    # potentially related recordings. This is only a validation grouping key.
    artist = (annotation.get("Artist") or "").strip()
    if not artist:
        title = annotation.get("Title") or ""
        artist = re.split(r"\s+[–—-]\s+", title, maxsplit=1)[0] if re.search(r"\s+[–—-]\s+", title) else ""
    return " ".join(artist.casefold().split()) or "<unknown>"


def load_reports(paths, with_phase=False):
    tracks = {}
    for path in paths:
        with zipfile.ZipFile(path) as archive:
            for entry in archive.namelist():
                if not entry.endswith("/annotations.json"):
                    continue
                annotation = json.loads(archive.read(entry))[0]
                ident = annotation["Id"].upper()
                if ident in tracks:
                    raise ValueError("Duplicate track SHA-256: " + ident)
                prefix = entry.removesuffix("annotations.json") + "diagnostics/"
                def read(name):
                    with archive.open(prefix + name + ".csv") as stream:
                        return pd.read_csv(stream)
                taps = np.sort(np.array([t["Frame"] for t in annotation["BeatTaps"]], dtype=np.int64))
                if not len(taps):
                    raise ValueError("No manual taps for " + ident)
                meta = archive.read(prefix + "metadata.txt").decode()
                match = re.search(r"Sample rate: (\d+); total PCM frames: (\d+)", meta)
                if not match or int(match[1]) != RATE or int(match[2]) <= 0:
                    raise ValueError("Unsupported PCM clock for " + ident)
                total = int(match[2])
                events = read("rhythm-events")
                event_frames = events.frame.to_numpy(dtype=np.int64)
                strengths = events[["strength", "kick_proxy", "snare_proxy",
                                    "hat_proxy", "bass_tail_proxy"]].to_numpy()
                baseline = read("audit-baseline-beats").frame.to_numpy(dtype=np.int64)
                if len(baseline) < 4 or not strictly_ordered(baseline) or not strictly_ordered(event_frames):
                    raise ValueError("Insufficient or unordered evidence for " + ident)
                if not np.isfinite(strengths).all():
                    raise ValueError("Nonfinite event evidence for " + ident)
                base_period = np.median(np.diff(baseline))
                candidates = [(name, read("beats" if name == "legacy" else name + "-beats"))
                              for name in CONTROLS]
                for name in ("metric-beats", "refined-metric-beats"):
                    candidates.extend((name + ":" + str(i), g) for i, g in read(name).groupby("id"))
                trace = phase_trace(read("audit-flux"), read("energy")) if with_phase else None
                rows = []
                for name, data in candidates:
                    beats = data.frame.to_numpy(dtype=np.int64)
                    if len(beats) < 4:
                        continue
                    if not strictly_ordered(beats):
                        raise ValueError("Unordered candidate " + ident + "/" + name)
                    x = features(beats, event_frames, strengths, total, base_period)
                    if trace is not None:
                        x = np.r_[x, phase_features(beats, trace)]
                    y = np.array([f1(beats, taps, ms) for ms in TOLERANCES])
                    rows.append((name, x, y))
                tracks[ident] = (annotation["Title"], rows, len(taps), artist_group(annotation))
    return tracks


def prepare(rows, all_paths):
    if not all_paths:
        rows = [r for r in rows if r[0] in CONTROLS[:6]]
    x = np.array([r[1] for r in rows])
    y = np.array([r[2] for r in rows])
    return rows, x - x.mean(0), y


def fit(tracks, all_paths):
    xs, ys, weights = [], [], []
    for _, rows, _, _ in tracks.values():
        _, x, y = prepare(rows, all_paths)
        xs.append(x)
        ys.append(y[:, TARGET_COLUMN] - y[:, TARGET_COLUMN].mean())
        weights.extend([1 / len(y)] * len(y))
    x, y = np.vstack(xs), np.concatenate(ys)
    w = np.array(weights) * len(y) / len(tracks)
    scaler = StandardScaler().fit(x, sample_weight=w)
    model = Ridge(alpha=10).fit(scaler.transform(x), y, sample_weight=w)
    return scaler, model


def evaluate(tracks, fitted, all_paths):
    scaler, model = fitted
    results = []
    for ident, (title, rows, count, artist) in tracks.items():
        rs, x, y = prepare(rows, all_paths)
        scores = model.predict(scaler.transform(x))
        i = int(np.argmax(scores))
        results.append({"id": ident, "title": title, "taps": count,
                        "artist_group": artist, "candidate": rs[i][0], "f1": y[i].tolist(),
                        "oracle": np.max(y, axis=0).tolist()})
    return results


def mean_f1(rows, key="f1"):
    return np.mean([r[key] for r in rows], axis=0).tolist()


def leave_artist_out(tracks, all_paths):
    results = []
    for artist in sorted({row[3] for row in tracks.values()}):
        held = {k: v for k, v in tracks.items() if v[3] == artist}
        others = {k: v for k, v in tracks.items() if v[3] != artist}
        if not others:
            return None
        results.extend(evaluate(held, fit(others, all_paths), all_paths))
    return results


def split_test(train, development, test, new_only):
    if train.keys() & development.keys():
        raise ValueError("Train/development SHA-256 IDs overlap.")
    known = train.keys() | development.keys()
    excluded = sorted(test.keys() & known)
    if excluded and not new_only:
        raise ValueError("Test overlaps known tracks; use --test-new-only for cumulative reports.")
    return {k: v for k, v in test.items() if k not in known}, excluded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--train", nargs="+", required=True, type=Path)
    parser.add_argument("--development", nargs="+", type=Path, default=[],
                        help="Previously inspected songs; never used to fit the model")
    parser.add_argument("--test", nargs="+", required=True, type=Path)
    parser.add_argument("--test-new-only", action="store_true",
                        help="Exclude known train/development IDs from cumulative test reports")
    parser.add_argument("--phase-features", action="store_true",
                        help="Experimental 16 additional onset/half-beat contrast features (not the default)")
    parser.add_argument("--output", type=Path, help="Optional private JSON evaluation/model output")
    args = parser.parse_args()
    train = load_reports(args.train, args.phase_features)
    development = load_reports(args.development, args.phase_features)
    test = load_reports(args.test, args.phase_features)
    try:
        test, excluded = split_test(train, development, test, args.test_new_only)
    except ValueError as error:
        parser.error(str(error))
    if len(train) < 3 or not test:
        parser.error("Need at least three training songs and one new test song.")
    report = {"feature_names": FEATURES + (PHASE_FEATURES if args.phase_features else ()),
              "tolerances_ms": TOLERANCES, "train_tracks": len(train),
              "development_tracks": len(development), "test_tracks": len(test),
              "excluded_known_test_ids": excluded, "experiments": []}
    print("Training", len(train), "development", len(development), "test", len(test), "excluded", len(excluded))
    for all_paths in (False, True):
        loo = []
        for ident in train:
            others = {k: v for k, v in train.items() if k != ident}
            loo.extend(evaluate({ident: train[ident]}, fit(others, all_paths), all_paths))
        fitted = fit(train, all_paths)
        result = evaluate(test, fitted, all_paths)
        dev = evaluate(development, fitted, all_paths)
        artist_loo = leave_artist_out(train, all_paths)
        scaler, model = fitted
        experiment = {"all_paths": all_paths, "loo_f1": mean_f1(loo),
                      "test_f1": mean_f1(result), "test_oracle": mean_f1(result, "oracle"),
                      "development_f1": mean_f1(dev) if dev else None,
                      "artist_loo_f1": mean_f1(artist_loo) if artist_loo else None,
                      "development_results": dev, "artist_loo_results": artist_loo,
                      "test_results": result, "loo_results": loo,
                      "model": {"alpha": 10, "mean": scaler.mean_.tolist(),
                                "scale": scaler.scale_.tolist(), "weights": model.coef_.tolist(),
                                "intercept": float(model.intercept_)}}
        report["experiments"].append(experiment)
        print("all_paths", all_paths, "LOO", experiment["loo_f1"],
              "test", experiment["test_f1"], "oracle", experiment["test_oracle"])
        print("artist LOO", experiment["artist_loo_f1"], "development", experiment["development_f1"])
        for row in result:
            print(row["title"], row["candidate"], row["f1"])
    if args.output:
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
