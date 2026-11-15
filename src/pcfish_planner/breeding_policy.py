"""Observed star distributions and material/parent utility for Protected mode.

These are pooled estimates and policy scores, not server probability guarantees.
The native rarity forecast remains authoritative for possible result levels.
"""
from collections import Counter, defaultdict
from functools import lru_cache
import math

from .learning import learn_rules
from .probabilities import breed_level_probabilities, merge_level_probabilities


class ObservationModel:
    def __init__(self, records=()):
        # Reuse validation and event-ID deduplication, including legacy receipts.
        records = [r for r in records if r.get('confirmed') is True
                   and not r.get('synthetic', False) and r.get('flow') != 'native_comparison'
                   and r.get('kind') in ('breed', 'merge')]
        learned = learn_rules(records, 'confirmed local game callbacks')
        self.events = learned['unique_events']
        self.grades = defaultdict(Counter)
        self.conditional = defaultdict(Counter)
        self.species = defaultdict(Counter)
        self.species_by_level = defaultdict(Counter)
        for rule in learned['rules']:
            key = rule['kind'], tuple(sorted(p['grade'] for p in rule['parents']))
            for outcome in rule['outcomes']:
                fish = outcome['fish']
                count = rule['sample_count'] * outcome['probability']
                result = fish['grade'], fish['remaining_breeds']
                self.grades[key][result] += count
                self.conditional[key, fish['level']][result] += count
                self.species[rule['kind'], fish['level'], fish['grade']][fish['species']] += count
                self.species_by_level[rule['kind'], fish['level']][fish['species']] += count

    @lru_cache(maxsize=256)
    def outcomes(self, kind, grades, level):
        pooled = self.grades[kind, grades]
        count = sum(pooled.values())
        if not count:
            # Untried pairs receive an explicit exploration heuristic. This is
            # deliberately reported without a grade probability forecast.
            grade = max(1, math.ceil(sum(grades) / len(grades)))
            return {(grade, 1): 1.0}, 0, 'unobserved_star_heuristic'
        conditional = self.conditional[(kind, grades), level]
        local_count = sum(conditional.values())
        # Twenty pooled observations shrink small result-level samples. Parent
        # order/species/remaining uses are pooled, not asserted to be irrelevant.
        prior = 20
        distribution = {outcome: (conditional[outcome] + prior * n / count) / (local_count + prior)
                        for outcome, n in pooled.items()}
        return distribution, round(count), 'observed_parent_stars'

    @lru_cache(maxsize=1024)
    def species_share(self, kind, level, grade, species, eligible):
        if species not in eligible:
            return 0.0
        pooled = self.species_by_level[kind, level]
        counts = self.species[kind, level, grade]
        # One pseudocount per catalog species prevents an unobserved species
        # from receiving zero weight. No parent-species inheritance is assumed.
        base = (pooled[species] + 1) / (sum(pooled[s] for s in eligible) + len(eligible))
        return (counts[species] + 20 * base) / (sum(counts[s] for s in eligible) + 20)


class MaterialScorer:
    """Value exact shortages and the remaining supply of useful future parents."""
    def __init__(self, snapshot, config, parents):
        self.model = config.get('_observation_model') or ObservationModel()
        self.needs = [n for n in config['_stock_plan']['needs'] if n['missing'] > 0]
        self.phase = config['_stock_plan'].get('phase')
        self.catalog = defaultdict(set)
        for fish in snapshot.get('species_catalog', []):
            if not fish['seasonal']:
                self.catalog[fish['level']].add(fish['species'])
        # Older snapshots may predate the complete species catalog.
        for fish in snapshot['fish']:
            if not fish['basic'] and not fish['seasonal']:
                self.catalog[fish['level']].add(fish['species'])
        for need in self.needs:
            self.catalog[need['level']].add(need['species'])
        self.catalog = {level: tuple(sorted(types)) for level, types in self.catalog.items()}
        self.supply = Counter()
        for fish in parents:
            if not fish['basic']:
                self.supply[fish['level'], fish['grade']] += fish['remaining_breeds']
        self.partner_levels = tuple(sorted({f['level'] for f in parents}))
        self.stage = min((n['grade'] for n in self.needs), default=0)
        self.stage_missing = sum(n['missing'] for n in self.needs if n['grade'] == self.stage)

    @lru_cache(maxsize=64)
    def material_value(self, kind, level, grade):
        score = 0.0
        for need in self.needs:
            if (need['level'], need['grade']) != (level, grade):
                continue
            value = 1.0
            if self.phase in ('collection', 'repeat') and grade == self.stage:
                # The final missing material unlocks the next recipe immediately.
                value += 2 / self.stage_missing
            share = self.model.species_share(kind, level, grade, need['species'], self.catalog[level])
            score += value * share
        return score

    @lru_cache(maxsize=32)
    def parent_value(self, level, grade):
        total_missing = sum(n['missing'] for n in self.needs)
        if not self.partner_levels or not total_missing:
            return 0.0
        # Value progress toward required stars even when five-star outcomes have
        # not occurred yet. Rarity compatibility uses actual available partners.
        return sum(n['missing'] * max(breed_level_probabilities(level, partner).get(n['level'], 0)
                                     for partner in self.partner_levels)
                   * min(grade / n['grade'], 1.0) ** 2 for n in self.needs) / total_missing

    def capacity_value(self, profile, count):
        # Diminishing returns prevent stockpiling low-star parents indefinitely.
        # Two remaining uses are the scale at which another parent loses urgency.
        return self.parent_value(*profile) * count / (count + 2)

    @lru_cache(maxsize=2048)
    def evaluate(self, kind, profiles):
        levels = tuple(p[0] for p in profiles)
        grades = tuple(sorted(p[1] for p in profiles))
        forecast = (breed_level_probabilities(*levels) if kind == 'breed'
                    else merge_level_probabilities(list(levels)))
        spent = Counter()
        for level, grade, uses in profiles:
            if level:
                spent[level, grade] += 1 if kind == 'breed' else uses
        remaining = {profile: max(0, self.supply[profile] - count) for profile, count in spent.items()}
        cost = sum(self.capacity_value(profile, self.supply[profile]) - self.capacity_value(profile, count)
                   for profile, count in remaining.items())
        direct = future = 0.0
        joint = []
        samples = 0
        basis = 'observed_parent_stars'
        for level, level_probability in forecast.items():
            if not level_probability:
                continue
            distribution, samples, basis = self.model.outcomes(kind, grades, level)
            for (grade, uses), star_probability in distribution.items():
                probability = level_probability * star_probability
                direct += probability * self.material_value(kind, level, grade)
                profile = level, grade
                available = remaining.get(profile, self.supply[profile])
                future += probability * (self.capacity_value(profile, available + uses)
                                         - self.capacity_value(profile, available))
                joint.append(dict(level=level, grade=grade, remaining_breeds=uses, weight=probability))
        # Half the value of a future opportunity discounts unknown growth time,
        # protection/price state and the uncertainty of subsequent offspring.
        parent_gain = 0.5 * (future - cost)
        return dict(score=direct + parent_gain, material_score=direct, parent_score=parent_gain,
                    sample_count=samples, evidence=basis, observed_events=self.model.events,
                    outcome_weights=joint, next_recipe_grade=self.stage)

    def batch(self, kind, fish):
        profiles = tuple(sorted((f['level'], f['grade'], f['remaining_breeds']) for f in fish))
        return self.evaluate(kind, profiles)
