"""Ordinary merge eligibility, independent of prices and species models."""
from collections.abc import Sequence


def merge_batch_allowed(levels: Sequence[int], grades: Sequence[int],
                        outcome_levels: Sequence[int] | None = None) -> bool:
    if len(levels) != 10 or len(grades) != 10:
        return False
    if any(isinstance(n, bool) or n not in range(1, 6) for n in (*levels, *grades)):
        return False
    highest = max(levels)
    if len(set(grades)) != 1 or highest - min(levels) > 1:
        return False
    # Lowest nonzero outcome in the inspected client forecast (Level 5 cap).
    # Keep this check explicit even though adjacent integer levels imply it.
    if min(5, sum(levels) // 10 + 1) < highest:
        return False
    # An economic model may be more pessimistic than the client display.
    if outcome_levels is not None and (not outcome_levels or min(outcome_levels) < highest):
        return False
    return True
