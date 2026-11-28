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
from sklearn.linear_model import Ridge
from sklearn.preprocessing import StandardScaler

RATE = 48000
CONTROLS = ("audit-baseline", "envelope-decay", "envelope-hybrid",
            "envelope-rise", "joint", "legacy", "envelope-refined-decay")
TOLERANCES = (50, 70, 100)
FEATURES = ("relative_log_period", "abs_relative_log_period", "log_bpm_120",
            "duration_coverage", "interval_mad", "long_gap_fraction",
            "event_coverage", *("event_alignment_" + s for s in
            ("strength", "kick", "snare", "hat", "tail")),
            *("beat_support_" + s for s in ("strength", "kick", "snare", "hat", "tail")),
            "beat_support_unweighted")


def f1(beats, taps, ms):
    """Chronological one-to-one matching inside annotated runs only."""
    tolerance = ms * RATE // 1000
    matched = predicted = 0
    for run in np.split(taps, np.flatnonzero(np.diff(taps) > 2 * RATE) + 1):
        selected = beats[(beats >= run[0] - tolerance) & (beats <= run[-1] + tolerance)]
        predicted += len(selected)
        i = j = 0
        while i < len(run) and j < len(selected):
            if selected[j] < run[i] - tolerance:
                j += 1
            elif run[i] < selected[j] - tolerance:
                i += 1
            else:
                matched += 1
                i += 1
                j += 1
    return 2 * matched / (predicted + len(taps))


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


def load_reports(paths):
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
                rows = []
                for name, data in candidates:
                    beats = data.frame.to_numpy(dtype=np.int64)
                    if len(beats) < 4:
                        continue
                    if not strictly_ordered(beats):
                        raise ValueError("Unordered candidate " + ident + "/" + name)
                    x = features(beats, event_frames, strengths, total, base_period)
                    y = np.array([f1(beats, taps, ms) for ms in TOLERANCES])
                    rows.append((name, x, y))
                tracks[ident] = (annotation["Title"], rows, len(taps))
    return tracks


def prepare(rows, all_paths):
    if not all_paths:
        rows = [r for r in rows if r[0] in CONTROLS[:6]]
    x = np.array([r[1] for r in rows])
    y = np.array([r[2] for r in rows])
    return rows, x - x.mean(0), y


def fit(tracks, all_paths):
    xs, ys, weights = [], [], []
    for _, rows, _ in tracks.values():
        _, x, y = prepare(rows, all_paths)
        xs.append(x)
        ys.append(y[:, 1] - y[:, 1].mean())
        weights.extend([1 / len(y)] * len(y))
    x, y = np.vstack(xs), np.concatenate(ys)
    w = np.array(weights) * len(y) / len(tracks)
    scaler = StandardScaler().fit(x, sample_weight=w)
    model = Ridge(alpha=10).fit(scaler.transform(x), y, sample_weight=w)
    return scaler, model


def evaluate(tracks, fitted, all_paths):
    scaler, model = fitted
    results = []
    for ident, (title, rows, count) in tracks.items():
        rs, x, y = prepare(rows, all_paths)
        scores = model.predict(scaler.transform(x))
        i = int(np.argmax(scores))
        results.append({"id": ident, "title": title, "taps": count,
                        "candidate": rs[i][0], "f1": y[i].tolist(),
                        "oracle": np.max(y, axis=0).tolist()})
    return results


def mean_f1(rows, key="f1"):
    return np.mean([r[key] for r in rows], axis=0).tolist()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--train", nargs="+", required=True, type=Path)
    parser.add_argument("--test", nargs="+", required=True, type=Path)
    parser.add_argument("--output", type=Path, help="Optional private JSON evaluation/model output")
    args = parser.parse_args()
    train, test = load_reports(args.train), load_reports(args.test)
    if len(train) < 3 or not test:
        parser.error("Need at least three training songs and one test song.")
    if train.keys() & test.keys():
        parser.error("Train/test SHA-256 IDs overlap.")
    report = {"feature_names": FEATURES, "tolerances_ms": TOLERANCES,
              "train_tracks": len(train), "test_tracks": len(test), "experiments": []}
    for all_paths in (False, True):
        loo = []
        for ident in train:
            others = {k: v for k, v in train.items() if k != ident}
            loo.extend(evaluate({ident: train[ident]}, fit(others, all_paths), all_paths))
        fitted = fit(train, all_paths)
        result = evaluate(test, fitted, all_paths)
        scaler, model = fitted
        experiment = {"all_paths": all_paths, "loo_f1": mean_f1(loo),
                      "test_f1": mean_f1(result), "test_oracle": mean_f1(result, "oracle"),
                      "test_results": result, "loo_results": loo,
                      "model": {"alpha": 10, "mean": scaler.mean_.tolist(),
                                "scale": scaler.scale_.tolist(), "weights": model.coef_.tolist(),
                                "intercept": float(model.intercept_)}}
        report["experiments"].append(experiment)
        print("all_paths", all_paths, "LOO", experiment["loo_f1"],
              "test", experiment["test_f1"], "oracle", experiment["test_oracle"])
        for row in result:
            print(row["title"], row["candidate"], row["f1"])
    if args.output:
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
