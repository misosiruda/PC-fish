from __future__ import annotations

from collections import Counter
from dataclasses import asdict, dataclass, replace
from hashlib import sha256
from itertools import combinations
import json
import math
from typing import Any


class ValidationError(ValueError):
    pass


def number(value: Any, name: str, minimum: float = 0) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < minimum:
        raise ValidationError(f"{name} must be a finite number >= {minimum}")
    return float(value)


def integer(value: Any, name: str, minimum: int = 0) -> int:
    number(value, name, minimum)
    if int(value) != value:
        raise ValidationError(f"{name} must be an integer")
    return int(value)


def flag(value: Any, name: str) -> bool:
    if not isinstance(value, bool):
        raise ValidationError(f"{name} must be boolean")
    return value


def text(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValidationError(f"{name} must be a nonempty string")
    return value


@dataclass(frozen=True)
class Fish:
    id: str
    species: str
    level: int
    grade: int
    remaining_breeds: int
    ready_at: float = 0
    unlimited_breeding: bool = False
    breed_allowed: bool = True
    merge_allowed: bool = True
    protected_breed: bool = False
    protected_merge: bool = False
    reserved: bool = False
    seasonal: bool = False
    breed_recovery_seconds: float | None = None

    @classmethod
    def read(cls, data: dict, *, id: str | None = None, now: float | None = None) -> Fish:
        if not isinstance(data, dict):
            raise ValidationError("fish must be an object")
        unknown = set(data) - cls.__dataclass_fields__.keys() - {'count', 'ready_after_seconds'}
        if unknown:
            raise ValidationError(f'unknown fish fields: {sorted(unknown)}')
        fields = {k: data[k] for k in cls.__dataclass_fields__ if k in data}
        if id is not None:
            fields['id'] = id
        if now is not None:
            fields['ready_at'] = now + number(data.get('ready_after_seconds', 0), 'ready_after_seconds')
        try:
            fish = cls(**fields)
        except TypeError as exc:
            raise ValidationError(f"incomplete fish: {exc}") from exc
        text(fish.id, 'fish.id')
        text(fish.species, 'fish.species')
        integer(fish.level, 'fish.level')
        integer(fish.grade, 'fish.grade')
        integer(fish.remaining_breeds, 'fish.remaining_breeds')
        number(fish.ready_at, 'fish.ready_at')
        if fish.breed_recovery_seconds is not None:
            number(fish.breed_recovery_seconds, 'fish.breed_recovery_seconds')
        if fish.level > 5 or fish.grade > 5 or (fish.level == 0) != (fish.grade == 0):
            raise ValidationError("fish level/grade must be 1..5, or both 0 for a starter")
        for key in ('unlimited_breeding', 'breed_allowed', 'merge_allowed', 'protected_breed',
                    'protected_merge', 'reserved', 'seasonal'):
            flag(getattr(fish, key), f'fish.{key}')
        if fish.unlimited_breeding and fish.level != 0:
            raise ValidationError("unlimited breeding requires an explicit level-0 starter")
        return fish

    def traits(self) -> tuple:
        # Parent order is deliberately represented as a multiset. See algorithm documentation.
        return (self.species, self.level, self.grade, self.remaining_breeds, self.unlimited_breeding)

    def can_breed(self, at: float) -> bool:
        return (self.breed_allowed and not self.protected_breed and self.ready_at <= at
                and (self.unlimited_breeding or self.remaining_breeds > 0))


@dataclass(frozen=True)
class State:
    now: float
    fish: tuple[Fish, ...]
    hearts: int
    next_heart_at: float
    serial: int = 0


@dataclass(frozen=True)
class Config:
    horizon_seconds: int = 1800
    max_actions: int = 3
    max_nodes: int = 10000
    max_parent_fish: int = 24
    max_breed_candidates: int = 16
    max_merge_candidates: int = 12
    max_outcomes: int = 16
    max_wait_events: int = 64
    heart_capacity: int = 5
    heart_interval_seconds: int = 420
    parent_cooldown_seconds: int = 180
    action_seconds: int = 1
    inventory_capacity: int = 999
    protect_merge_grade_at_least: int = 5
    allow_seasonal_merge: bool = False
    allow_assumptions: bool = False
    min_model_samples: int = 30
    max_quote_age_seconds: int = 86400
    discount_rate_per_hour: float = 0.0
    action_cost: float = 0.0
    minimum_gain: float = 0.01

    @classmethod
    def read(cls, data: dict) -> Config:
        unknown = set(data) - cls.__dataclass_fields__.keys()
        if unknown:
            raise ValidationError(f"unknown configuration keys: {sorted(unknown)}")
        config = cls(**data)
        bools = {'allow_seasonal_merge', 'allow_assumptions'}
        floats = {'discount_rate_per_hour', 'action_cost', 'minimum_gain'}
        for key in cls.__dataclass_fields__:
            value = getattr(config, key)
            if key in bools:
                flag(value, key)
            elif key in floats:
                number(value, key)
            else:
                integer(value, key, 1)
        if config.protect_merge_grade_at_least > 6:
            raise ValidationError("protect_merge_grade_at_least must be 1..6 (6 disables grade protection)")
        if config.max_actions > 8 or config.max_nodes > 100000 or config.horizon_seconds > 86400 or config.max_wait_events > 256:
            raise ValidationError("planning limits: actions<=8, nodes<=100000, horizon<=86400, wait events<=256")
        return config


@dataclass(frozen=True)
class Quote:
    species: str
    grade: int
    remaining_breeds: int
    net_proceeds: float
    sale_probability: float
    captured_at: float
    source: str
    evidence: str


@dataclass(frozen=True)
class Outcome:
    probability: float
    fish: dict


@dataclass(frozen=True)
class Rule:
    id: str
    kind: str
    parents: tuple[tuple, ...]
    outcomes: tuple[Outcome, ...]
    evidence: str
    source: str
    sample_count: int


class MissingData(Exception):
    pass


class Problem:
    """Validated input boundary. Monetary values are seller net, in one declared currency."""

    def __init__(self, payload: dict):
        if not isinstance(payload, dict):
            raise ValidationError("input must be an object")
        if payload.get('schema_version') != 1:
            raise ValidationError("schema_version must be 1")
        self.currency = text(payload.get('currency'), 'currency')
        self.synthetic = flag(payload.get('synthetic', False), 'synthetic')
        self.config = Config.read(payload.get('config', {}))
        raw_state = payload.get('state', {})
        now = number(raw_state.get('now'), 'state.now')
        fish = tuple(sorted((Fish.read(x) for x in raw_state.get('fish', [])), key=lambda f: f.id))
        if len({f.id for f in fish}) != len(fish):
            raise ValidationError("duplicate fish IDs")
        if any(f.id.startswith('planned:') for f in fish):
            raise ValidationError("planned: is reserved for simulated IDs")
        if len(fish) > self.config.inventory_capacity:
            raise ValidationError("inventory exceeds configured capacity")
        hearts = integer(raw_state.get('hearts'), 'state.hearts')
        if hearts > self.config.heart_capacity:
            raise ValidationError("hearts exceed capacity")
        next_heart = number(raw_state.get('next_heart_at'), 'state.next_heart_at')
        self.state = State(now, fish, hearts, next_heart)
        species_levels = {}

        def check_species(fish: Fish):
            if species_levels.setdefault(fish.species, fish.level) != fish.level:
                raise ValidationError(f'inconsistent level for species: {fish.species}')

        for item in fish:
            check_species(item)
        self.busy = flag(raw_state.get('request_in_flight', False), 'request_in_flight')
        self.uncertain = flag(raw_state.get('last_result_uncertain', False), 'last_result_uncertain')
        self.quotes: dict[tuple, Quote] = {}
        for data in payload.get('quotes', []):
            if data.get('currency', self.currency) != self.currency:
                raise ValidationError('all quotes must use the declared currency')
            q = Quote(text(data.get('species'), 'quote.species'), integer(data.get('grade'), 'quote.grade', 1),
                      integer(data.get('remaining_breeds'), 'quote.remaining_breeds'),
                      number(data.get('net_proceeds'), 'quote.net_proceeds'),
                      number(data.get('sale_probability'), 'quote.sale_probability'),
                      number(data.get('captured_at'), 'quote.captured_at'),
                      text(data.get('source'), 'quote.source'), text(data.get('evidence'), 'quote.evidence'))
            if q.grade > 5 or q.sale_probability > 1 or q.captured_at > now:
                raise ValidationError("invalid quote grade, sale probability, or future capture time")
            if q.evidence not in {'observed', 'verified', 'assumption', 'synthetic'}:
                raise ValidationError("unknown quote evidence")
            key = (q.species, q.grade, q.remaining_breeds)
            if key in self.quotes:
                raise ValidationError(f"duplicate quote: {key}")
            self.quotes[key] = q
        self.rules: dict[tuple, Rule] = {}
        ids = set()
        for data in payload.get('rules', []):
            kind = data.get('kind')
            if kind not in {'breed', 'merge'}:
                raise ValidationError("rule.kind must be breed or merge")
            parents = []
            for item in data.get('parents', []):
                count = integer(item.get('count', 1), 'parent count', 1)
                if len(parents) + count > (2 if kind == 'breed' else 10):
                    raise ValidationError('too many parents in rule')
                template = Fish.read(item, id='template')
                check_species(template)
                parents.extend([template.traits()] * count)
            if len(parents) != (2 if kind == 'breed' else 10):
                raise ValidationError(f"{kind} rule requires {'2' if kind == 'breed' else '10'} parents")
            if len(data.get('outcomes', [])) > self.config.max_outcomes:
                raise ValidationError("outcome count exceeds max_outcomes; no silent probability truncation")
            outcomes = []
            for item in data.get('outcomes', []):
                probability = number(item.get('probability'), 'outcome.probability')
                if probability <= 0 or probability > 1:
                    raise ValidationError("outcome probabilities must be positive and <=1")
                if not isinstance(item.get('fish'), dict) or 'ready_after_seconds' not in item['fish']:
                    raise ValidationError("outcomes require explicit ready_after_seconds")
                template = Fish.read(item.get('fish'), id='result', now=now)
                check_species(template)
                if template.level == 0:
                    raise ValidationError("actions cannot generate starter outcomes")
                if template.seasonal:
                    raise ValidationError("seasonal outcomes require season crafting, not regular breed/merge")
                outcomes.append(Outcome(probability, dict(item['fish'])))
            if not outcomes or not math.isclose(sum(x.probability for x in outcomes), 1.0, abs_tol=1e-9):
                raise ValidationError("joint outcome probabilities must sum to 1; unknown mass is not discarded")
            rule = Rule(text(data.get('id'), 'rule.id'), kind, tuple(sorted(parents)), tuple(outcomes),
                        text(data.get('evidence'), 'rule.evidence'), text(data.get('source'), 'rule.source'),
                        integer(data.get('sample_count', 0), 'rule.sample_count'))
            if rule.evidence not in {'observed', 'verified', 'assumption', 'synthetic'}:
                raise ValidationError("unknown rule evidence")
            key = (kind, rule.parents)
            if key in self.rules or rule.id in ids:
                raise ValidationError("duplicate rule ID or parent multiset")
            self.rules[key] = rule
            ids.add(rule.id)
        self.input_digest = sha256(json.dumps(payload, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


@dataclass(frozen=True)
class Action:
    kind: str
    fish_ids: tuple[str, ...] = ()
    until: float | None = None


@dataclass(frozen=True)
class Decision:
    gain: float
    action: Action
    immediate_gain: float = 0.0


class Planner:
    """Bounded event-driven expectimax. Returns only the next recommendation."""

    def __init__(self, problem: Problem):
        self.p = problem
        self.c = problem.config
        self.end = problem.state.now + self.c.horizon_seconds
        self.nodes = 0
        self.limits: set[str] = set()
        self.missing: Counter[str] = Counter()
        self.used_assumptions = False
        self._memo: dict[tuple, Decision] = {}

    def advance(self, state: State, at: float) -> State:
        if at < state.now:
            raise ValidationError("time cannot move backwards")
        hearts, due = state.hearts, state.next_heart_at
        if hearts < self.c.heart_capacity and due <= at:
            # Native BreedTimer keeps its timestamp while capped. At most one overdue
            # refill is credited at the current instant, then its timestamp resets.
            first = max(due, state.now)
            earned = min(self.c.heart_capacity - hearts, 1 + int((at - first) // self.c.heart_interval_seconds))
            hearts += earned
            due = first + earned * self.c.heart_interval_seconds
        return replace(state, now=at, hearts=hearts, next_heart_at=due)

    def trusted(self, evidence: str, samples: int | None = None) -> bool:
        if evidence in {'assumption', 'synthetic'}:
            if not self.c.allow_assumptions:
                return False
            self.used_assumptions = True
        if evidence == 'observed' and samples is not None and samples < self.c.min_model_samples:
            return False
        return True

    def value(self, fish: Fish) -> float:
        if fish.level == 0:
            return 0.0  # Explicit nonmarketable starter; never inferred from missing quotes.
        key = (fish.species, fish.grade, fish.remaining_breeds)
        quote = self.p.quotes.get(key)
        if quote is None:
            raise MissingData(f"quote_missing:{fish.species}:grade{fish.grade}:breeds{fish.remaining_breeds}")
        if self.p.state.now - quote.captured_at > self.c.max_quote_age_seconds:
            raise MissingData(f"quote_stale:{quote.source}")
        if not self.trusted(quote.evidence):
            raise MissingData(f"quote_untrusted:{quote.source}")
        return quote.net_proceeds * quote.sale_probability

    def rule(self, kind: str, parents: tuple[Fish, ...]) -> Rule:
        key = (kind, tuple(sorted(f.traits() for f in parents)))
        rule = self.p.rules.get(key)
        if rule is None:
            raise MissingData(f"joint_outcome_model_missing:{kind}")
        if not self.trusted(rule.evidence, rule.sample_count):
            raise MissingData(f"joint_outcome_model_untrusted:{rule.id}")
        return rule

    def transitions(self, state: State, action: Action) -> tuple[tuple[float, float, State], ...]:
        mapping = {f.id: f for f in state.fish}
        try:
            parents = tuple(mapping[id] for id in action.fish_ids)
        except KeyError as exc:
            raise ValidationError("action refers to missing fish") from exc
        if len(set(action.fish_ids)) != len(action.fish_ids):
            raise ValidationError("a fish cannot be consumed twice")
        if action.kind == 'breed':
            if len(parents) != 2 or state.hearts < 1 or len(state.fish) >= self.c.inventory_capacity:
                raise ValidationError("invalid breeding action")
            if any(not f.can_breed(state.now) for f in parents):
                raise ValidationError("parent unavailable")
        elif action.kind == 'merge':
            if len(parents) != 10 or any(not self.merge_eligible(f) for f in parents):
                raise ValidationError("invalid merge action")
        else:
            raise ValidationError("transition requires breed or merge")
        rule = self.rule(action.kind, parents)
        completion = state.now + self.c.action_seconds
        if completion > self.end:
            raise ValidationError("action completes outside horizon")
        others = [f for f in state.fish if f.id not in action.fish_ids]
        cost = 0.0
        if action.kind == 'breed':
            for fish in parents:
                if fish.unlimited_breeding and fish.breed_recovery_seconds is None:
                    raise MissingData('starter_growth_recovery_model_missing')
                recovery = fish.breed_recovery_seconds if fish.breed_recovery_seconds is not None else self.c.parent_cooldown_seconds
                changed = replace(fish, remaining_breeds=fish.remaining_breeds if fish.unlimited_breeding else fish.remaining_breeds - 1,
                                  ready_at=completion + recovery)
                cost += self.value(fish) - self.value(changed)
                others.append(changed)
        else:
            cost = sum(self.value(fish) for fish in parents)
        results = []
        for index, outcome in enumerate(rule.outcomes):
            new_id = f'planned:{state.serial}:{rule.id}:{index}'
            if new_id in mapping:
                raise ValidationError("generated ID collides with input; reserve the planned: prefix")
            child = Fish.read(outcome.fish, id=new_id, now=completion)
            reward = self.value(child) - cost - self.c.action_cost
            clock = self.advance(state, completion)
            # UIBreed's successful callback resets the timestamp before spending
            # from full hearts. BreedTimer alone does not show this reset.
            due = completion + self.c.heart_interval_seconds if action.kind == 'breed' and clock.hearts == self.c.heart_capacity else clock.next_heart_at
            next_state = State(completion, tuple(sorted([*others, child], key=lambda f: f.id)),
                               clock.hearts - (action.kind == 'breed'), due, state.serial + 1)
            next_state = self.advance(next_state, completion)
            results.append((outcome.probability, reward, next_state))
        return tuple(results)

    def merge_eligible(self, fish: Fish) -> bool:
        return (fish.merge_allowed and not fish.protected_merge and not fish.reserved
                and not fish.unlimited_breeding and fish.level > 0
                and fish.grade < self.c.protect_merge_grade_at_least
                and (self.c.allow_seasonal_merge or not fish.seasonal))

    def candidates(self, state: State) -> list[tuple[Action, tuple]]:
        actions = []
        ready = [f for f in state.fish if f.can_breed(state.now)]
        # Two interchangeable representatives suffice for a pair; retain earlier-ready fish.
        groups: dict[tuple, list[Fish]] = {}
        for fish in sorted(ready, key=lambda f: (-f.level, -f.remaining_breeds, f.grade, f.id)):
            bucket = groups.setdefault(fish.traits(), [])
            if len(bucket) < 2:
                bucket.append(fish)
        pool = [f for bucket in groups.values() for f in bucket]
        if len(pool) > self.c.max_parent_fish:
            self.limits.add('parent_pool_pruned')
            pool = pool[:self.c.max_parent_fish]
        if state.hearts and len(state.fish) < self.c.inventory_capacity:
            actions.extend(Action('breed', tuple(sorted((a.id, b.id)))) for a, b in combinations(pool, 2))
        # Merge candidates come from complete modeled multisets, avoiding C(n,10).
        eligible: dict[tuple, list[Fish]] = {}
        for fish in state.fish:
            if self.merge_eligible(fish):
                eligible.setdefault(fish.traits(), []).append(fish)
        for rule in self.p.rules.values():
            if rule.kind != 'merge':
                continue
            ids = []
            for traits, count in Counter(rule.parents).items():
                available = eligible.get(traits, [])
                if len(available) < count:
                    break
                ids.extend(f.id for f in available[:count])
            else:
                actions.append(Action('merge', tuple(sorted(ids))))
        if sum(len(group) for group in eligible.values()) >= 10 and not any(a.kind == 'merge' for a in actions):
            self.missing['joint_outcome_model_missing:merge'] += 1
        scored = {'breed': [], 'merge': []}
        if state.now + self.c.action_seconds > self.end:
            return []
        for action in actions:
            try:
                transitions = self.transitions(state, action)
            except MissingData as exc:
                self.missing[str(exc)] += 1
                continue
            immediate = sum(p * reward for p, reward, _ in transitions)
            scored[action.kind].append((immediate, action, transitions))
        result = []
        for kind, limit in [('breed', self.c.max_breed_candidates), ('merge', self.c.max_merge_candidates)]:
            values = sorted(scored[kind], key=lambda x: (-x[0], x[1].fish_ids))
            if len(values) > limit:
                self.limits.add(f'{kind}_candidates_pruned')
            result.extend((a, t) for _, a, t in values[:limit])
        return result

    def next_event(self, state: State) -> float | None:
        events = [f.ready_at for f in state.fish if state.now < f.ready_at < self.end
                  and f.breed_allowed and not f.protected_breed and (f.remaining_breeds > 0 or f.unlimited_breeding)]
        if state.hearts < self.c.heart_capacity and state.now < state.next_heart_at < self.end:
            events.append(state.next_heart_at)
        return min(events) if events else None

    def discount(self, at: float) -> float:
        return math.exp(-self.c.discount_rate_per_hour * (at - self.p.state.now) / 3600)

    def search(self, state: State, remaining: int, events_left: int | None = None) -> Decision:
        if events_left is None:
            events_left = self.c.max_wait_events
        key = (state, remaining, events_left)
        if key in self._memo:
            return self._memo[key]
        if self.nodes >= self.c.max_nodes:
            self.limits.add('node_budget_reached')
            return Decision(0.0, Action('hold'))
        self.nodes += 1
        best = Decision(0.0, Action('hold'))
        if remaining > 0 and state.now < self.end:
            for action, transitions in self.candidates(state):
                immediate = sum(p * r for p, r, _ in transitions)
                gain = sum(p * (self.discount(next_state.now) * reward + self.search(next_state, remaining - 1, events_left).gain)
                           for p, reward, next_state in transitions)
                if gain > best.gain + 1e-9:
                    best = Decision(gain, action, immediate)
            event = self.next_event(state)
            if event is not None and events_left > 0:
                future = self.search(self.advance(state, event), remaining, events_left - 1)
                if future.gain > best.gain + 1e-9:
                    best = Decision(future.gain, Action('wait', until=event))
            elif event is not None:
                self.limits.add('wait_event_limit_reached')
        self._memo[key] = best
        return best

    def run(self) -> dict:
        initial = self.advance(self.p.state, self.p.state.now)
        interlock = 'request_in_flight' if self.p.busy else 'last_result_uncertain' if self.p.uncertain else None
        alternatives = []
        if interlock:
            best = Decision(0.0, Action('hold'))
        else:
            best = self.search(initial, self.c.max_actions)
            # Report alternatives using the same completed search/memo; no second search budget.
            for action, transitions in self.candidates(initial):
                immediate = sum(p * r for p, r, _ in transitions)
                gain = sum(p * (self.discount(s.now) * r + self._memo.get((s, self.c.max_actions - 1, self.c.max_wait_events), Decision(0, Action('hold'))).gain)
                           for p, r, s in transitions)
                alternatives.append({'action': asdict(action), 'expected_gain': round(gain, 6),
                                     'immediate_gain': round(immediate, 6)})
            event = self.next_event(initial)
            if event is not None:
                future = self._memo.get((self.advance(initial, event), self.c.max_actions, self.c.max_wait_events - 1))
                alternatives.append({'action': asdict(Action('wait', until=event)),
                                     'expected_gain': round(future.gain, 6) if future else None, 'immediate_gain': 0})
            if best.gain < self.c.minimum_gain:
                best = Decision(0.0, Action('hold'))
        action = asdict(best.action)
        protected = [f.id for f in initial.fish if not self.merge_eligible(f)]
        requirements = [{'issue': key, 'occurrences': count} for key, count in self.missing.most_common(12)]
        status = 'simulation' if self.p.synthetic or self.used_assumptions else 'recommendation'
        if interlock:
            status = 'hold_pending_reconciliation'
        elif best.action.kind == 'hold' and requirements:
            status = 'hold_missing_data'
        reason = {
            'breed': '현재 부모 가치 감소와 자식 가치를 포함한 계획 기대값이 대기·보존보다 높습니다.',
            'merge': '재료 10마리의 남은 교배 횟수를 포함한 기회비용을 차감한 계획 기대값이 대기·보존보다 높습니다.',
            'wait': '다음 사건까지 기다린 뒤 가능한 행동의 계획 기대값이 지금 실행하는 행동보다 높습니다.',
            'hold': '양의 기대 이익을 확인한 행동이 없거나, 평가·상태 확인에 필요한 데이터가 부족합니다.',
        }[best.action.kind]
        if interlock:
            reason = '진행 중인 요청 또는 결과가 불명확한 작업의 상태를 먼저 확인해야 합니다.'
        return {'schema_version': 1, 'status': status, 'execution_supported': False,
                'currency': self.p.currency, 'input_sha256': self.p.input_digest,
                'action': action, 'reason': reason,
                'reevaluate_at': None if interlock else best.action.until if best.action.kind == 'wait' else (self.next_event(initial) or self.end),
                'expected_inventory_value_gain': round(best.gain, 6),
                'immediate_gain': round(best.immediate_gain, 6),
                'horizon_end': self.end, 'max_mutating_actions': self.c.max_actions,
                'alternatives': sorted(alternatives, key=lambda x: -(x['expected_gain'] or 0))[:10],
                'merge_protected_fish_ids': protected, 'data_requirements': requirements,
                'search': {'nodes': self.nodes, 'limits': sorted(self.limits), 'globally_optimal': False},
                'assumptions_used': self.used_assumptions,
                'interlock': interlock,
                'notes': ['다음 행동만 추천합니다. 실제 응답 후 새 상태로 다시 계획해야 합니다.',
                          '예상 재고 가치 변화이며 실현된 판매 수익이 아닙니다.',
                          '입력의 판매 확률·공동 결과 모델이 정확하다는 보장은 없습니다.']}
