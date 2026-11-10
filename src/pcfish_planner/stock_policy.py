"""Consume native stock allocations; rarity forecasts never imply species/Grade odds."""
from collections import Counter

from .engine import ValidationError
from .merge_policy import merge_batch_allowed
from .probabilities import merge_level_probabilities


def validate_plan(plan, fish):
    if plan.get('policy_version') != 2:
        raise ValidationError('Season stock policy requires matching native plugin (policy version 2)')
    known = {f['id'] for f in fish}
    for key in ('reserved_ids', 'exhausted_ids', 'surplus_ids', 'parent_ids'):
        ids = plan.get(key)
        if (not isinstance(ids, list) or any(not isinstance(i, str) or i not in known for i in ids)
                or len(set(ids)) != len(ids)):
            raise ValidationError(f'invalid native stock allocation: {key}')
    if set(plan['reserved_ids']) & (set(plan['exhausted_ids']) | set(plan['surplus_ids'])):
        raise ValidationError('native stock candidates overlap reservations')
    needs = plan.get('needs')
    if not isinstance(needs, list):
        raise ValidationError('invalid native stock needs')
    seen = set()
    for n in needs:
        if (not isinstance(n, dict) or not isinstance(n.get('species'), str) or not n['species']
                or any(type(n.get(k)) is not int for k in ('level', 'grade', 'required', 'reserved', 'missing'))
                or n['level'] not in range(1, 6) or n['grade'] not in range(1, 6)
                or not 0 <= n['reserved'] <= n['required'] or n['missing'] != n['required'] - n['reserved']
                or (n['species'], n['grade']) in seen):
            raise ValidationError('invalid native stock need')
        seen.add((n['species'], n['grade']))


def rarity_weights(config):
    plan = config.get('_stock_plan')
    if plan is None:
        return (0, 1, 2, 4, 10, 25)
    # Average fractional shortages per rarity; do not invent uniform species or
    # star probabilities. This is a policy preference over client rarity forecasts.
    total, slots = Counter(), Counter()
    for n in plan['needs']:
        if n['required'] > 0:
            total[n['level']] += n['missing'] / n['required']
            slots[n['level']] += 1
    return tuple(total[level] / slots[level] if slots[level] else 0 for level in range(6))


def merge_allowed(snapshot, config, batch):
    if not merge_batch_allowed([f['level'] for f in batch], [f['grade'] for f in batch]):
        return False
    plan = config.get('_stock_plan')
    if plan is None:
        return all(f['remaining_breeds'] == 0 for f in batch)
    ids = {f['id'] for f in batch}
    if ids & set(plan['reserved_ids']):
        return False
    surplus = set(plan['surplus_ids'])
    if len({f['level'] for f in batch}) == 1 and ids <= surplus:
        zero = sum(f['id'] in surplus and f['level'] == batch[0]['level'] and f['grade'] == batch[0]['grade']
                   and f['remaining_breeds'] == 0 for f in snapshot['fish'])
        return sum(f['remaining_breeds'] == 0 for f in batch) >= min(10, zero)
    if any(f['remaining_breeds'] != 0 or f['id'] not in plan['exhausted_ids'] for f in batch):
        return False
    forecast = merge_level_probabilities([f['level'] for f in batch])
    return any(n['missing'] > 0 and forecast.get(n['level'], 0) > 0 for n in plan['needs'])


def merge_utility(config, batch):
    weights = rarity_weights(config)
    return sum(weights[level] * probability for level, probability in merge_level_probabilities([f['level'] for f in batch]).items())


def material_gain(before, after, needs, initial):
    """Exact useful inventory growth for an economic model's verified outcomes."""
    a = Counter((f.species, f.grade) for f in before.fish)
    b = Counter((f.species, f.grade) for f in after.fish)
    base = Counter((f.species, f.grade) for f in initial.fish)
    def progress(counts, n):
        key = n['species'], n['grade']
        return min(n['missing'], max(0, counts[key] - base[key]))
    return sum(progress(b, n) - progress(a, n) for n in needs)
