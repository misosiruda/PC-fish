"""Client display forecasts, not a joint species/Grade server probability model."""
from .engine import ValidationError, integer

# Active CSV/Breed/breed_level_v1.0, Steam build 26315872, inspected 2026-11-06.
BREED_WEIGHTS = (
    (10000, 0, 0, 0, 0), (9000, 1000, 0, 0, 0), (7600, 2000, 400, 0, 0),
    (4887, 4163, 950, 0, 0), (3772, 4428, 1500, 300, 0), (0, 5049, 4301, 650, 0),
    (0, 4048, 4752, 1000, 200), (0, 0, 5211, 4439, 350), (0, 0, 4370, 5130, 500),
    (0, 0, 0, 7000, 3000), (0, 0, 0, 4500, 5500),
)


def breed_level_probabilities(first: int, second: int) -> dict[int, float]:
    if integer(first, 'first level') > 5 or integer(second, 'second level') > 5:
        raise ValidationError('parent levels must be 0..5')
    return {i: n / 10000 for i, n in enumerate(BREED_WEIGHTS[first + second], 1)}


def merge_level_probabilities(levels: list[int]) -> dict[int, float]:
    if len(levels) != 10 or any(integer(x, 'material level', 1) > 5 for x in levels):
        raise ValidationError('merge requires ten levels in 1..5')
    total = sum(levels)
    lower = min(5, total // 10 + 1)
    upper = min(5, (total + 9) // 10 + 1)
    result = dict.fromkeys(range(1, 6), 0.0)
    if lower == upper:
        result[lower] = 1.0
    else:
        fraction = (total % 10) / 10
        result[lower] = (10 - total % 10) / 10
        result[upper] = fraction
    return result
