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


def protected(fish: dict, config: dict) -> bool:
    return (fish['locked'] or fish['seasonal'] or fish['grade'] >= config.get('protect_grade_at_least', 3)
            or fish['id'] in config.get('reserved_ids', []) or fish['species'] in config.get('reserved_species', []))


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
    """Bounded known-parent lookahead; utility is rarity progress, never money.

    Newborn species/Grade and starter regrowth times are unknown: do not invent
    them. Replan immediately after every actual outcome instead.
    """
    now = s['captured_at']
    pool = [f for f in s['fish'] if not protected(f, config)]
    if config.get('auto_merge', True):
        materials = [f for f in pool if not f['basic'] and f['remaining_breeds'] == 0 and f['level'] > 0]
        groups = defaultdict(list)
        for f in materials:
            groups[(f['level'], f['grade'])].append(f)
        batches = [sorted(g, key=lambda f: f['id'])[:10] for g in groups.values() if len(g) >= 10]
        if not batches and len(materials) >= 10:
            batches = [sorted(materials, key=lambda f: (-f['level'], f['grade'], f['id']))[:10]]
        if batches:
            batch = max(batches, key=lambda b: (sum(f['level'] for f in b), -sum(f['grade'] for f in b)))
            return {'kind': 'merge', 'fish_ids': [f['id'] for f in batch],
                    'reason': 'Exhausted ordinary fish batch; protected Grade/season/locks excluded',
                    'forecast_levels': merge_level_probabilities([f['level'] for f in batch]),
                    'objective': 'protected_rarity_progress', 'profit_estimate': None}
    if not config.get('auto_breed', True) or len(s['fish']) >= s['capacity']:
        return {'kind': 'hold', 'reason': 'Breeding disabled or inventory full'}
    candidates = [f for f in pool if (f['basic'] and f['ready']) or (not f['basic'] and f['remaining_breeds'] > 0)]
    # Include both current and future high-level parents. Keep at least two of
    # each interchangeable (level, remaining, readiness) group, then bound work.
    buckets = defaultdict(list)
    for f in sorted(candidates, key=lambda f: (-f['level'], max(0, f['ready_at'] - now), -f['remaining_breeds'], f['id'])):
        key = (f['level'], f['remaining_breeds'], f['ready_at'], f['basic'])
        if len(buckets[key]) < 2:
            buckets[key].append(f)
    candidates = [f for bucket in buckets.values() for f in bucket][:24]
    if len(candidates) < 2:
        return {'kind': 'hold', 'reason': 'Waiting for two unprotected mature parents'}
    horizon = 900
    # The weights are policy preferences, NOT estimated sale prices or Grade odds.
    weights = (0, 1, 2, 4, 10, 25)
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
            pairs = sorted(combinations(ready, 2), key=lambda ij: -utility[candidates[ij[0]]['level'], candidates[ij[1]]['level']])[:16]
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
        if events:
            event = min(events)
            score, _ = solve(event, hearts, due, state, actions)
            if score > best[0] + 1e-9:
                best = score, ('wait', (), event)
        return best

    score, (kind, selected, at) = solve(0, s['hearts'], max(0, math.ceil(s['next_heart_at'] - now)), initial, min(3, s['capacity'] - len(s['fish'])))
    result = {'kind': kind, 'fish_ids': [candidates[i]['id'] for i in selected], 'objective': 'protected_rarity_progress',
              'profit_estimate': None, 'lookahead_utility': score, 'search_nodes': nodes, 'search_limited': limited,
              'reason': 'Compare current parents against cooldown/heart events; no Grade or price distribution assumed'}
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
                       'unlimited_breeding': f['basic'], 'protected_breed': protected(f, config) or not config.get('auto_breed', True),
                       'protected_merge': protected(f, config) or not config.get('auto_merge', True), 'seasonal': f['seasonal']})
    payload['schema_version'] = 1
    payload['state'] = {'now': s['captured_at'], 'fish': fishes, 'hearts': s['hearts'], 'next_heart_at': s['next_heart_at']}
    payload.setdefault('config', {}).update(allow_assumptions=False, inventory_capacity=s['capacity'],
                                          protect_merge_grade_at_least=config.get('protect_grade_at_least', 3), allow_seasonal_merge=False)
    recommendation = Planner(Problem(payload)).run()
    return {**recommendation['action'], 'reason': recommendation['reason'], 'economic_details': recommendation}


def decide(s: dict, config: dict, now: float) -> dict:
    validate_snapshot(s)
    if now - s['captured_at'] > 5 or s['captured_at'] > now + 2:
        return {'kind': 'hold', 'reason': 'Snapshot stale'}
    if not config.get('enabled') or config.get('mode') == 'observe':
        return {'kind': 'hold', 'reason': 'Observation mode'}
    if s['request_in_flight'] or s['uncertain']:
        return {'kind': 'hold', 'reason': 'Game request in progress or unresolved result'}
    if s['ui_busy']:
        return {'kind': 'hold', 'reason': s.get('ui_block_reason') or 'Waiting for actual game UI interaction'}
    if config.get('mode') == 'protected':
        return protected_plan(s, config)
    if config.get('mode') == 'economic':
        return economic_plan(s, config)
    raise ValidationError('Unknown live mode')


def run(runtime: Path, config_path: Path, session: str) -> None:
    last_observation_count = -1
    started = time.monotonic()
    while True:
        try:
            snapshot_path = runtime / 'snapshot.json'
            if not snapshot_path.exists():
                if time.monotonic() - started > 30:
                    return
                time.sleep(1)
                continue
            s = read_json(snapshot_path)
            if s['session'] != session or time.time() - s['captured_at'] > 30:
                return  # Game stopped/restarted; no orphan worker.
            raw = read_text(config_path)
            cfg = json.loads(raw)
            digest = sha256(raw.encode('utf-8')).hexdigest()
            if digest != s['config_digest']:
                time.sleep(1)
                continue
            events = sorted((runtime / 'events').glob('*.json'))
            if len(events) != last_observation_count:
                records = [read_json(p) for p in events]
                if records:
                    atomic(runtime / 'observed-rules.json', learn_rules(records, 'confirmed local game callbacks'))
                last_observation_count = len(events)
            plan = decide(s, cfg, time.time())
            atomic(runtime / 'recommendation.json', {'captured_at': time.time(), 'session': session, 'revision': s['revision'], **plan})
            command_path = runtime / 'command.json'
            if plan['kind'] in ('breed', 'merge') and not (runtime / 'pending.json').exists() and not command_path.exists():
                atomic(command_path, {'request_id': str(uuid.uuid4()), 'session': session, 'revision': s['revision'],
                                     'config_digest': digest, 'expires_at': time.time() + 5, 'kind': plan['kind'],
                                     'fish_ids': plan['fish_ids'], 'reason': plan['reason']})
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
