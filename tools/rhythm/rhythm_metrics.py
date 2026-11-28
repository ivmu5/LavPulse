"""Metrics for sparse manual observations on the LavPulse PCM clock."""
import numpy as np


def series_counts(beats, taps, tolerance, rate):
    """Return match, prediction and tap counts within tap series.

    Inputs must be sorted. The chronological greedy match maximizes cardinality
    at fixed tolerance. Use timing_errors for minimum-error residuals.
    """
    if not len(taps):
        return 0, 0, 0
    matches = predicted = 0
    prediction_end = 0
    for run in np.split(taps, np.flatnonzero(np.diff(taps) > 2 * rate) + 1):
        first = max(prediction_end, np.searchsorted(beats, run[0] - tolerance, side="left"))
        prediction_end = np.searchsorted(beats, run[-1] + tolerance, side="right")
        predicted += prediction_end - first
        i = first
        j = 0
        while i < prediction_end and j < len(run):
            delta = run[j] - beats[i]
            if delta > tolerance:
                i += 1
            elif delta < -tolerance:
                j += 1
            else:
                matches += 1
                i += 1
                j += 1
    return matches, predicted, len(taps)


def timing_errors(beats, taps, tolerance):
    """Signed tap-minus-beat residuals for minimum-error maximum matching.

    Process each legal (beat, tap) edge once. The Fenwick tree stores the
    best chain ending in a prefix of taps. Decreasing tap order within
    a beat prevents an edge from reusing that same beat.
    """
    tree = [0] * (len(taps) + 1)
    counts, costs, previous, errors = [0], [0], [0], [0]

    def better(a, b):
        return (counts[a], -costs[a]) > (counts[b], -costs[b])

    def query(index):
        best = 0
        while index:
            if better(tree[index], best):
                best = tree[index]
            index &= index - 1
        return best

    lower = upper = 0
    for beat in beats:
        while lower < len(taps) and taps[lower] < beat - tolerance:
            lower += 1
        upper = max(lower, upper)
        while upper < len(taps) and taps[upper] <= beat + tolerance:
            upper += 1
        for j in range(upper - 1, lower - 1, -1):
            parent = query(j)
            delta = int(taps[j] - beat)
            node = len(errors)
            counts.append(counts[parent] + 1)
            costs.append(costs[parent] + abs(delta))
            previous.append(parent)
            errors.append(delta)
            k = j + 1
            while k < len(tree):
                if better(node, tree[k]):
                    tree[k] = node
                k += k & -k

    result = []
    node = query(len(taps))
    while node:
        result.append(errors[node])
        node = previous[node]
    return np.asarray(result[::-1], dtype=np.int64)
