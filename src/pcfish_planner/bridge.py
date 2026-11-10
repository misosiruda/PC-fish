"""Local IL2CPP mod bridge. No screen input or direct game-server requests.

Protected mode is an explicitly nonmonetary bootstrap policy. Economic mode uses
the existing strict joint-outcome/quote planner and never falls back silently.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from functools import lru_cache
from hashlib import sha256
from itertools import combinations
import json
import math
import os
from pathlib import Path
import time
import uuid

from .engine import Planner, Problem, ValidationError
from .learning import learn_rules
from .probabilities import breed_level_probabilities, merge_level_probabilities
from .market import MarketMonitor, market_settings
from .merge_policy import merge_batch_allowed
from .stock_policy import validate_plan as validate_stock_plan, rarity_weights, merge_allowed, merge_utility, material_gain


def atomic(path: Path, value: dict) -> None:
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    # Windows readers can briefly hold a handle without FILE_SHARE_DELETE.
    # Retry only the local rename; never retry a game request here.
    for attempt in range(20):
        try:
            os.replace(temp, path)
            return
        except PermissionError:
            if attempt == 19:
                raise
            time.sleep(.005)


def read_text(path: Path) -> str:
    """Read a local IPC file across a short Windows replacement/share conflict."""
    for attempt in range(20):
        try:
            # Match .NET StreamReader: strip an optional UTF-8 BOM, preserve
            # CRLF exactly so the two processes compute the same config hash.
            with path.open('r', encoding='utf-8-sig', newline='') as stream:
                return stream.read()
        except (PermissionError, FileNotFoundError):
            if attempt == 19:
                raise
            time.sleep(.005)


def read_json(path: Path) -> dict:
    return json.loads(read_text(path))


def learning_records(paths: list[Path]) -> list[dict]:
    # Bounded comparisons deliberately alter observation timing. Keep their
    # immutable operational evidence separate from normal planner learning.
    return [record for path in paths if (record := read_json(path)).get('flow') != 'native_comparison']


def recent_rejected_actions(runtime: Path, now: float) -> dict[str, list[list[str]]]:
    """Rotate exact combinations after explicit, nonconsumptive rejections."""
    batches = {'merge': [], 'breed': []}
    for path in (runtime / 'rejected-events').glob('*.json'):
        try:
            record = read_json(path)
            at = record.get('completed_at')
            kind = record.get('kind')
            ids = record.get('cmd', {}).get('fish_ids', [])
            expected = {'merge': 10, 'breed': 2}.get(kind)
            if (expected and record.get('outcome') == 'rejected' and record.get('consumed') is False
                    and type(at) in (int, float) and math.isfinite(at) and 0 <= now - at < 600
                    and isinstance(ids, list) and len(ids) == expected and all(isinstance(x, str) and x for x in ids)
                    and len(set(ids)) == expected):
                batches[kind].append(ids)
        except (OSError, ValueError, TypeError, AttributeError):
            continue  # Optional scheduling history cannot stop normal automation.
    return batches


def recent_rejected_merges(runtime: Path, now: float) -> list[list[str]]:
    return recent_rejected_actions(runtime, now)['merge']


def recent_unpersisted_breed_pairs(runtime: Path, now: float) -> list[list[str]]:
    """Try other exact pairs while a recent unapplied starter receipt is tracked."""
    pairs = []
    verified = {}
    for path in (runtime / 'events').glob('*.json'):
        try:
            event = read_json(path)
            ids, at = event.get('fish_ids', []), event.get('completed_at')
            if (event.get('kind') == 'breed' and event.get('confirmed') is True
                    and isinstance(ids, list) and len(ids) == 2 and all(isinstance(id, str) and id for id in ids)
                    and type(at) in (int, float) and math.isfinite(at) and at <= now):
                key = frozenset(ids)
                verified[key] = max(verified.get(key, 0), at)
        except (OSError, ValueError, TypeError, AttributeError):
            continue
    for path in (runtime / 'deferred-receipts').glob('*.json'):
        try:
            record = read_json(path)
            cmd = record.get('cmd', {})
            dispatched, observed = record.get('dispatched_at'), record.get('absence_last_at', 0)
            if type(dispatched) not in (int, float) or type(observed) not in (int, float):
                continue
            at, ids = max(dispatched, observed), cmd.get('fish_ids', [])
            if (cmd.get('kind') == 'breed' and record.get('response_status') == 'success'
                    and not record.get('accounting_started') and not record.get('local_commit_started')
                    and type(at) in (int, float) and math.isfinite(at) and 0 <= now - at < 600
                    and isinstance(ids, list) and len(ids) == 2 and len(set(ids)) == 2
                    and all(isinstance(id, str) and id for id in ids) and verified.get(frozenset(ids), 0) <= at):
                pairs.append(ids)
        except (OSError, ValueError, TypeError, AttributeError):
            continue
    return pairs


def protected(fish: dict, config: dict, now: float | None = None, *, action: str = 'breed') -> bool:
    market_mode = market_settings(config)[0] == 'market'
    now = time.time() if now is None else now
    product_value = action == 'merge' and config.get('market_merge_price_basis') in ('full_breeds', 'best_observed_breeds', 'highest_buy_order')
    state = fish.get('market_merge_state' if product_value else 'market_state')
    valid_until = fish.get('market_merge_valid_until' if product_value else 'market_valid_until', 0)
    price_protected = (not fish['basic'] and (state != 'release'
                       or type(valid_until) not in (int, float) or not math.isfinite(valid_until)
                       or valid_until <= now)) if market_mode else fish['grade'] >= config.get('protect_grade_at_least', 3)
    return (fish['locked'] or fish['seasonal'] or price_protected
            or fish['id'] in config.get('reserved_ids', []) or fish['species'] in config.get('reserved_species', [])
            or action == 'merge' and fish['id'] in config.get('_season_reserved_ids', []))


def validate_snapshot(s: dict) -> None:
    for field in ('captured_at', 'next_heart_at'):
        if isinstance(s[field], bool) or not isinstance(s[field], (int, float)) or not math.isfinite(s[field]):
            raise ValidationError(f'invalid snapshot {field}')
    if type(s['hearts']) is not int or not 0 <= s['hearts'] <= 5 or type(s['capacity']) is not int or s['capacity'] < len(s['fish']):
        raise ValidationError('invalid snapshot capacity/hearts')
    ids = set()
    for f in s['fish']:
        if not f['id'] or f['id'] in ids:
            raise ValidationError('duplicate/empty inventory ID')
        ids.add(f['id'])
        for name in ('level', 'grade', 'remaining_breeds', 'growth'):
            if type(f[name]) is not int or f[name] < 0:
                raise ValidationError('invalid inventory integer')
        if f['level'] > 5 or f['grade'] > 5 or (f['level'] == 0) != (f['grade'] == 0):
            raise ValidationError('invalid rarity/grade')
        for name in ('ready', 'basic', 'locked', 'seasonal', 'placed'):
            if type(f[name]) is not bool:
                raise ValidationError('invalid inventory flag')
        if f['basic'] != (f['species'] == 'FS00000') or (f['level'] == 0) != f['basic']:
            raise ValidationError('unknown starter identity')
        if not math.isfinite(f['ready_at']) or f['ready_at'] < 0:
            raise ValidationError('invalid parent ready time')


def protected_plan(s: dict, config: dict) -> dict:
    # Spend a full heart before routine inventory reduction so repeated merges
    # cannot starve breeding and waste regeneration. Season crafts are handled
    # separately by decide() and keep their existing precedence.
    prefer_breeding = bool(config.get('_stock_plan')) or s['hearts'] == 5 or s['hearts'] > 0 and config.get('_deferred_merge_batches')
    breeding = _protected_breed_plan(s, config) if prefer_breeding else None
    if breeding is not None and breeding['kind'] == 'breed':
        if not config.get('_stock_plan'):
            breeding['reason'] = ('Hearts full; breed now to resume regeneration' if s['hearts'] == 5
                                  else 'Recent merge rejection; perform available breeding before another merge')
        return breeding
    merge = _protected_merge_plan(s, config)
    if merge is not None:
        return merge
    return breeding if breeding is not None else _protected_breed_plan(s, config)


def _protected_merge_plan(s: dict, config: dict) -> dict | None:
    now = s['captured_at']
    if config.get('auto_merge', True):
        materials = [f for f in s['fish'] if not f['basic'] and f['level'] > 0
                     and not protected(f, config, now, action='merge')
                     and (f['remaining_breeds'] == 0 or f['id'] in config.get('_stock_plan', {}).get('surplus_ids', []))]
        groups = defaultdict(list)
        for f in materials:
            groups[(f['level'], f['grade'])].append(f)
        material_order = lambda f: (f['remaining_breeds'], -f['level'], f['grade'], f['id'])
        rejected = [frozenset(ids) for ids in config.get('_deferred_merge_batches', [])]

        def batches_for(group):
            ordered = sorted(group, key=material_order)
            return [ordered[i:i + 10] for i in range(len(ordered) - 9)
                    if frozenset(f['id'] for f in ordered[i:i + 10]) not in rejected
                    and merge_allowed(s, config, ordered[i:i + 10])]

        batches = [batch for group in groups.values() for batch in batches_for(group)]
        if not batches or config.get('_stock_plan'):
            seen = {frozenset(f['id'] for f in batch) for batch in batches}
            for grade in {f['grade'] for f in materials}:
                for lower in range(1, 5):
                    adjacent = [f for f in materials if f['grade'] == grade and lower <= f['level'] <= lower + 1]
                    for batch in batches_for(adjacent):
                        ids = frozenset(f['id'] for f in batch)
                        if ids not in seen and merge_batch_allowed([f['level'] for f in batch], [f['grade'] for f in batch]):
                            batches.append(batch)
                            seen.add(ids)
        if batches:
            batch = max(batches, key=lambda b: (merge_utility(config, b),
                        -max((len(set(f['id'] for f in b) & ids) for ids in rejected), default=0),
                        len({f['level'] for f in b}) == 1, -sum(f['remaining_breeds'] for f in b), sum(f['level'] for f in b)))
            return {'kind': 'merge', 'fish_ids': [f['id'] for f in batch],
                    'reason': 'Preserve stock and parents; exhausted materials target missing rarity, or same-rarity triple-surplus batch with exhausted fish first',
                    'forecast_levels': merge_level_probabilities([f['level'] for f in batch]),
                    'objective': 'material_rarity_progress' if config.get('_stock_plan') else 'protected_rarity_progress', 'profit_estimate': None}
    return None


def _protected_breed_plan(s: dict, config: dict) -> dict:
    """Bounded known-parent lookahead; utility is rarity progress, never money.

    Newborn species/Grade and starter regrowth times are unknown: do not invent
    them. Replan immediately after every actual outcome instead.
    """
    now = s['captured_at']
    pool = [f for f in s['fish'] if not protected(f, config, now)]
    if not config.get('auto_breed', True) or len(s['fish']) >= s['capacity']:
        return {'kind': 'hold', 'reason': 'Breeding disabled or inventory full'}
    candidates = [f for f in pool if (f['basic'] and f['ready']) or (not f['basic'] and f['remaining_breeds'] > 0)]
    # Include both current and future high-level parents. Keep at least two of
    # each interchangeable (level, remaining, readiness) group, then bound work.
    rejected = {frozenset(ids) for ids in config.get('_deferred_breed_pairs', [])}
    rejected_ids = {id for pair in rejected for id in pair}
    buckets = defaultdict(list)
    for f in sorted(candidates, key=lambda f: (-f['level'], max(0, f['ready_at'] - now), -f['remaining_breeds'], f['id'] in rejected_ids, f['id'])):
        key = (f['level'], f['remaining_breeds'], f['ready_at'], f['basic'])
        # Rejected identities are no longer interchangeable. Retain them as
        # separate candidates along with two representatives never rejected.
        if len(buckets[key]) < 2 or f['id'] in rejected_ids:
            buckets[key].append(f)
    by_level = defaultdict(list)
    for bucket in buckets.values():
        for f in bucket:
            by_level[f['level']].append(f)
    # Keep representatives from EVERY rarity: high-only truncation can discard
    # the low-rarity parents needed for current material shortages.
    candidates = []
    for _, group in sorted(by_level.items()):
        representatives = [f for f in group if f['id'] not in rejected_ids][:2]
        representatives += [f for f in group if f not in representatives][:4 - len(representatives)]
        candidates.extend(representatives)
    if len(candidates) < 2:
        return {'kind': 'hold', 'reason': 'Waiting for two unprotected mature parents'}
    horizon = 900
    # The weights are policy preferences, NOT estimated sale prices or Grade odds.
    weights = rarity_weights(config)
    utility = {(a, b): sum(weights[level] * p for level, p in breed_level_probabilities(a, b).items())
               for a in range(6) for b in range(6)}
    initial = tuple((max(0, math.ceil(f['ready_at'] - now)), 1 if f['basic'] else f['remaining_breeds']) for f in candidates)
    nodes = 0
    limited = False

    @lru_cache(maxsize=5000)
    def solve(at, hearts, due, state, actions):
        nonlocal nodes, limited
        nodes += 1
        if nodes > 4000:
            limited = True
            return 0.0, ('hold', (), at)
        if hearts < 5 and due <= at:
            hearts += 1
            due = at + 420
        if actions == 0 or at >= horizon:
            return 0.0, ('hold', (), at)
        best = (0.0, ('hold', (), at))
        if hearts:
            ready = [i for i, (ready_at, count) in enumerate(state) if count > 0 and ready_at <= at]
            pairs = sorted((ij for ij in combinations(ready, 2)
                            if frozenset(candidates[i]['id'] for i in ij) not in rejected),
                           key=lambda ij: -utility[candidates[ij[0]]['level'], candidates[ij[1]]['level']])[:16]
            for i, j in pairs:
                changed = list(state)
                for index in (i, j):
                    changed[index] = (at + 180, state[index][1] - 1)
                # Normal UI resets timestamp when spending from full hearts.
                next_due = at + 420 if hearts == 5 else due
                future, _ = solve(at + 1, hearts - 1, next_due, tuple(changed), actions - 1)
                # Earlier high-quality outcomes create earlier opportunities to
                # replan/use the real newborn. This timing preference is explicit.
                score = math.exp(-at / horizon) * utility[candidates[i]['level'], candidates[j]['level']] + future
                if score > best[0] + 1e-9:
                    best = score, ('breed', (i, j), at)
        events = [ready_at for ready_at, count in state if count and at < ready_at < horizon]
        if hearts < 5 and at < due < horizon:
            events.append(due)
        if events and not (at == 0 and s['hearts'] == 5 and best[1][0] == 'breed'):
            event = min(events)
            score, _ = solve(event, hearts, due, state, actions)
            if score > best[0] + 1e-9:
                best = score, ('wait', (), event)
        return best

    score, (kind, selected, at) = solve(0, s['hearts'], max(0, math.ceil(s['next_heart_at'] - now)), initial, min(3, s['capacity'] - len(s['fish'])))
    result = {'kind': kind, 'fish_ids': [candidates[i]['id'] for i in selected], 'objective': 'material_rarity_progress' if config.get('_stock_plan') else 'protected_rarity_progress',
              'profit_estimate': None, 'lookahead_utility': score, 'search_nodes': nodes, 'search_limited': limited,
              'reason': 'Target missing material rarities; species/star odds unknown' if config.get('_stock_plan') else 'Compare current parents against cooldown/heart events; no Grade or price distribution assumed'}
    if kind == 'wait':
        result['until'] = now + at
    if kind == 'breed':
        result['forecast_levels'] = breed_level_probabilities(*(candidates[i]['level'] for i in selected))
    return result


def economic_plan(s: dict, config: dict) -> dict:
    model_file = config.get('economic_model_file')
    if not model_file:
        return {'kind': 'hold', 'reason': 'Economic model/quotes missing; no bootstrap fallback in economic mode'}
    payload = read_json(Path(model_file))
    if payload.get('synthetic'):
        raise ValidationError('synthetic data cannot execute in the live game')
    fishes = []
    for f in s['fish']:
        fishes.append({'id': f['id'], 'species': f['species'], 'level': f['level'], 'grade': f['grade'],
                       'remaining_breeds': f['remaining_breeds'], 'ready_at': f['ready_at'] if not f['basic'] or f['ready'] else s['captured_at'] + 86400,
                       'unlimited_breeding': f['basic'], 'protected_breed': protected(f, config, s['captured_at']) or not config.get('auto_breed', True),
                       'protected_merge': protected(f, config, s['captured_at'], action='merge') or not config.get('auto_merge', True)
                       or f['remaining_breeds'] > 0 and f['id'] not in config.get('_stock_plan', {}).get('surplus_ids', []), 'seasonal': f['seasonal']})
    payload['schema_version'] = 1
    payload['state'] = {'now': s['captured_at'], 'fish': fishes, 'hearts': s['hearts'], 'next_heart_at': s['next_heart_at']}
    payload.setdefault('config', {}).update(allow_assumptions=False, inventory_capacity=s['capacity'],
                                          protect_merge_grade_at_least=6 if market_settings(config)[0] == 'market' else config.get('protect_grade_at_least', 3), allow_seasonal_merge=False)
    if config.get('_stock_plan'):
        # The live stock objective is a material count, so a currency threshold
        # from an economic model must not suppress useful material production.
        payload['config']['minimum_gain'] = 0
    class LivePlanner(Planner):
        def parent_order(self, fish):
            if not config.get('_stock_plan'):
                return super().parent_order(fish)
            targets = {(n['species'], n['grade']) for n in config['_stock_plan']['needs'] if n['missing'] > 0}
            useful = max((sum(o.probability for o in r.outcomes if (o.fish['species'], o.fish['grade']) in targets)
                          for r in self.p.rules.values() if r.kind == 'breed' and fish.traits() in r.parents
                          and self.trusted(r.evidence, r.sample_count)), default=0)
            return (-useful, *super().parent_order(fish))

        def action_allowed(self, state, action):
            if action.kind != 'merge':
                return True
            # Native reservations are valid for the observed inventory only.
            # Replan merges after a real result rather than spending hypothetical stock.
            if state.serial != 0:
                return False
            selected = [f for f in s['fish'] if f['id'] in action.fish_ids]
            return merge_allowed(s, config, selected)

        def transitions(self, state, action):
            outcomes = super().transitions(state, action)
            if not config.get('_stock_plan'):
                return outcomes
            needs = config['_stock_plan']['needs']
            # Economic inputs still have to pass the existing quote/joint-model
            # validation. With a stock goal, useful materials take priority over money.
            return tuple((p, material_gain(state, after, needs, self.p.state), after) for p, _, after in outcomes)

    recommendation = LivePlanner(Problem(payload)).run()
    if config.get('_stock_plan'):
        recommendation['expected_material_gain'] = recommendation.pop('expected_inventory_value_gain')
        recommendation['immediate_material_gain'] = recommendation.pop('immediate_gain')
        recommendation['objective'] = 'verified_material_shortage_reduction'
        recommendation['reason'] = 'Verified species/star outcomes compared for current material shortages; no profit claim'
        recommendation.pop('currency', None)
        recommendation['alternatives'] = []
        recommendation['notes'] = ['Uses verified joint outcomes and quotes; score is useful material count, not monetary gain.']
    return {**recommendation['action'], 'reason': recommendation['reason'], 'economic_details': recommendation}


def decide(s: dict, config: dict, now: float) -> dict:
    validate_snapshot(s)
    market_settings(config)
    if now - s['captured_at'] > 5 or s['captured_at'] > now + 2:
        return {'kind': 'hold', 'reason': 'Snapshot stale'}
    if not config.get('enabled') or config.get('mode') == 'observe':
        return {'kind': 'hold', 'reason': 'Observation mode'}
    if s['request_in_flight'] or s['uncertain']:
        return {'kind': 'hold', 'reason': 'Game request in progress or unresolved result'}
    if s['ui_busy']:
        return {'kind': 'hold', 'reason': s.get('ui_block_reason') or 'Waiting for actual game UI interaction'}
    if config.get('mode') not in ('protected', 'economic'):
        raise ValidationError('Unknown live mode')
    season_details = None
    if config.get('auto_season_craft', False):
        season = s.get('season')
        if not isinstance(season, dict) or not isinstance(season.get('plan'), dict):
            return {'kind': 'hold', 'reason': 'Authoritative season recipe plan unavailable'}
        season_details = season['plan']
        state = season_details.get('state')
        if state not in ('inactive', 'complete', 'waiting', 'ready'):
            return {'kind': 'hold', 'reason': season_details.get('reason', 'Season recipe plan unavailable')}
        if season_details.get('policy_version') != 2:
            return {'kind': 'hold', 'reason': 'Season stock policy requires the updated native plugin (policy version 2)'}
        validate_stock_plan(season_details, s['fish'])
        ids = season_details.get('reserved_ids')
        if not isinstance(ids, list) or any(not isinstance(id, str) for id in ids):
            raise ValidationError('invalid season material reservations')
        # Recipe reservations preserve the material itself. Breeding retains
        # the parent and only spends a use, so do not turn these into manual locks.
        config = {**config, '_season_reserved_ids': ids, '_stock_plan': season_details}
        craft = season_details.get('next_craft')
        if state == 'ready':
            if not isinstance(craft, dict) or not craft.get('fish_ids') or craft.get('season') != season.get('id'):
                raise ValidationError('invalid authoritative season craft')
            return {'kind': 'craft', 'fish_ids': craft['fish_ids'], 'season': craft['season'],
                    'target_species': craft['species'], 'target_grade': craft['grade'],
                    'reason': season_details['reason'], 'season_details': season_details}
    if config.get('mode') == 'protected' and s.get('breed_retry_after_server_restore', False):
        retry = _protected_breed_plan(s, config)
        if retry['kind'] == 'breed':
            retry['reason'] = 'Native server-data restoration completed; perform a new guarded breeding request'
            if season_details is not None:
                retry['season_details'] = season_details
            return retry
    if config.get('mode') == 'protected':
        plan = protected_plan(s, config)
    elif config.get('mode') == 'economic':
        plan = economic_plan(s, config)
    else:
        raise ValidationError('Unknown live mode')
    if season_details is not None:
        plan['season_details'] = season_details
    return plan


def run(runtime: Path, config_path: Path, session: str) -> None:
    last_observation_count = -1
    started = time.monotonic()
    market = MarketMonitor(runtime)
    while True:
        try:
            snapshot_path = runtime / 'snapshot.json'
            if not snapshot_path.exists():
                if time.monotonic() - started > 30:
                    market.close()
                    return
                time.sleep(1)
                continue
            s = read_json(snapshot_path)
            if s['session'] != session or time.time() - s['captured_at'] > 30:
                market.close()
                return  # Game stopped/restarted; no orphan worker.
            raw = read_text(config_path)
            cfg = json.loads(raw)
            digest = sha256(raw.encode('utf-8')).hexdigest()
            if digest != s['config_digest']:
                time.sleep(1)
                continue
            if market_settings(cfg)[0] == 'market' and cfg.get('enabled') and cfg.get('mode') != 'observe':
                validate_snapshot(s)
                market.update(s, cfg, time.time())
            events = sorted((runtime / 'events').glob('*.json'))
            if len(events) != last_observation_count:
                records = learning_records(events)
                if records:
                    atomic(runtime / 'observed-rules.json', learn_rules(records, 'confirmed local game callbacks'))
                last_observation_count = len(events)
            rejected = recent_rejected_actions(runtime, time.time())
            cfg['_deferred_merge_batches'] = rejected['merge']
            cfg['_deferred_breed_pairs'] = rejected['breed'] + recent_unpersisted_breed_pairs(runtime, time.time())
            # Market/evidence work can outlast the five-second action freshness
            # window. Plan from a fresh native capture, retaining session/config
            # checks rather than extending the validity of the old snapshot.
            s = read_json(snapshot_path)
            if s['session'] != session:
                market.close()
                return
            if s['config_digest'] != digest or read_text(config_path) != raw:
                time.sleep(1)
                continue
            plan = decide(s, cfg, time.time())
            if cfg.get('auto_season_craft', False):
                atomic(runtime / 'season-review.json', {'captured_at': time.time(), 'session': session,
                       'revision': s['revision'], 'season': s.get('season'), 'decision': plan['kind']})
            atomic(runtime / 'recommendation.json', {'captured_at': time.time(), 'session': session, 'revision': s['revision'], **plan})
            command_path = runtime / 'command.json'
            if plan['kind'] in ('breed', 'merge', 'craft') and not (runtime / 'pending.json').exists() and not command_path.exists():
                atomic(command_path, {'request_id': str(uuid.uuid4()), 'session': session, 'revision': s['revision'],
                                     'config_digest': digest, 'expires_at': time.time() + 5, 'kind': plan['kind'],
                                     'fish_ids': plan['fish_ids'], 'reason': plan['reason'],
                                     **({key: plan[key] for key in ('season', 'target_species', 'target_grade')} if plan['kind'] == 'craft' else {})})
            atomic(runtime / 'worker-status.json', {'captured_at': time.time(), 'state': 'running', 'mode': cfg.get('mode'), 'decision': plan['kind']})
        except Exception as exc:
            atomic(runtime / 'worker-status.json', {'captured_at': time.time(), 'state': 'error', 'error': f'{type(exc).__name__}: {exc}'})
        time.sleep(2)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', required=True, type=Path)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--pid', required=True, type=int)  # Diagnostic only; freshness/session control lifetime.
    parser.add_argument('--session', required=True)
    args = parser.parse_args()
    run(args.runtime, args.config, args.session)
