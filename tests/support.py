from copy import deepcopy


def fish(id, *, level=3, grade=1, species=None, breeds=1, ready=0, **flags):
    return {'id': id, 'species': species or f'species{level}', 'level': level, 'grade': grade,
            'remaining_breeds': breeds, 'ready_at': ready, **flags}


def quote(species, grade=1, breeds=0, value=0, **changes):
    return {'species': species, 'grade': grade, 'remaining_breeds': breeds, 'net_proceeds': value,
            'sale_probability': 1, 'captured_at': 0, 'source': 'synthetic test fixture', 'evidence': 'synthetic', **changes}


def result(species='child', level=4, grade=1, breeds=0, ready=420, **flags):
    return {'species': species, 'level': level, 'grade': grade, 'remaining_breeds': breeds,
            'ready_after_seconds': ready, **flags}


def rule(id, parents, outcomes, kind='breed', **changes):
    return {'id': id, 'kind': kind, 'parents': deepcopy(parents),
            'outcomes': [{'probability': p, 'fish': child} for p, child in outcomes],
            'evidence': 'synthetic', 'sample_count': 0, 'source': 'synthetic test fixture', **changes}


def problem(fishes, rules=(), quotes=(), **config):
    return {'schema_version': 1, 'currency': 'KRW', 'synthetic': True,
            'state': {'now': 0, 'hearts': 1, 'next_heart_at': 360, 'fish': deepcopy(fishes)},
            'config': {'allow_assumptions': True, 'horizon_seconds': 300, 'max_actions': 1, **config},
            'rules': deepcopy(list(rules)), 'quotes': deepcopy(list(quotes))}


def wait_example():
    fishes = [fish('low-a'), fish('low-b'), fish('high-a', level=4, ready=120), fish('high-b', level=5, ready=120)]
    quotes = [quote(f'species{level}', breeds=remaining, value=10) for level in [3, 4, 5] for remaining in [0, 1]]
    values = {2: 1, 3: 2, 4: 20, 5: 100}
    quotes += [quote(f'child{level}', value=value) for level, value in values.items()]
    low = rule('pair-3-3', fishes[:2], [(p, result(f'child{level}', level=level)) for level, p in [(2, .4048), (3, .4752), (4, .1), (5, .02)]])
    high = rule('pair-4-5', fishes[2:], [(.7, result('child4', level=4)), (.3, result('child5', level=5))])
    return problem(fishes, [low, high], quotes)


def merge_example(expensive=False):
    level, grade, value = (5, 5, 1000) if expensive else (1, 1, 1)
    fishes = [fish(f'material{i}', level=level, grade=grade, species='material', breeds=0) for i in range(10)]
    output = result('merged', level=min(5, level + 1), grade=grade, breeds=1)
    model = rule('merge-material', fishes, [(1, output)], kind='merge')
    return problem(fishes, [model], [quote('material', grade, value=value), quote('merged', grade, breeds=1, value=1500 if expensive else 40)])
