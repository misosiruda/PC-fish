"""Offline native-plan -> Python-decision -> native-guard contract replay.

Run after building tests/ModGuard. Only temporary JSON files and the test
executable are used; no game process, server, or runtime command path is used.
"""
import argparse
from collections import defaultdict
import csv
import json
from pathlib import Path
import subprocess
import uuid

from pcfish_planner.bridge import decide, protected
from pcfish_planner.stock_policy import merge_allowed


def normal(id, species='A', level=1, grade=1, breeds=0):
    return dict(id=id, species=species, level=level, grade=grade, remaining_breeds=breeds, maximum_breeds=5,
                ready_at=0, ready=breeds > 0, basic=False, locked=False, seasonal=False, placed=False, growth=10,
                market_state='release', market_merge_state='release', market_valid_until=9999, market_merge_valid_until=9999)


def case(fishes, active=False):
    recipes = [dict(season=2, species='S', grade=g, ingredients=[
        dict(species='A' if g == 1 else 'S', grade=max(1, g-1), amount=1, seasonal=g > 1),
        dict(species='C', grade=g, amount=3, seasonal=False)]) for g in range(1, 6)]
    return dict(config=dict(enabled=True, mode='protected', auto_season_craft=True, season_goal='collection',
                            protection_mode='market', market_merge_price_basis='highest_buy_order'),
                snapshot=dict(captured_at=1000, next_heart_at=1420, hearts=0, capacity=999, fish=fishes,
                              species_catalog=[dict(species=s, name=s, level=l, seasonal=s == 'S') for s,l in [('A',1),('B',2),('C',3),('S',5)]],
                              season=dict(id=2, active=active, starts_at=100, ends_at=9999, recipes=recipes)))


def run(args):
    out = args.artifact_dir.resolve(); out.mkdir(parents=True, exist_ok=True)
    cases = [case([normal(str(i), grade=g, breeds=b) for i in range(n)])
             for n,g,b in [(44,1,3),(45,1,3),(9,5,0),(13,5,0),(60,1,0)]]
    cases.append(case([normal(str(i), breeds=0 if i < 25 else 3) for i in range(45)]))
    cases.append(case([normal('a'+str(i), breeds=3) for i in range(45)]
                      + [normal('b'+str(i), 'B', 2, breeds=3) for i in range(45)]))
    cases.append(case([normal(str(i), 'B', 2) for i in range(10)], active=True))
    two = normal('season-two', 'S', 5, grade=2); two['seasonal'] = True
    cases.append(case([two]+[normal(f'c{g}-{i}', 'C', 3, g) for g in [3,4,5] for i in range(3)], active=True))
    if args.archived_snapshot:
        snapshot = json.loads(args.archived_snapshot.read_text(encoding='utf-8-sig'))
        types = list(csv.DictReader(args.species_table.open(encoding='utf-8-sig')))
        snapshot['species_catalog'] = [dict(species=r['type'], name=r['type'], level=int(r['level']), seasonal=int(r['season']) > 0)
                                       for r in types if int(r['level']) > 0]
        cases.append(dict(snapshot=snapshot, config=case([])['config']))

    def native(stage, values):
        source, target = out/f'{stage}-input.json', out/f'{stage}-output.json'
        source.write_text(json.dumps(values, ensure_ascii=False), encoding='utf-8')
        subprocess.run([str(args.dotnet), str(args.native_dll.resolve()), '--policy-replay', str(source), str(target)], check=True,
                       capture_output=True, text=True, encoding='utf-8')
        return json.loads(target.read_text(encoding='utf-8'))

    planned = native('plan', cases)
    expected = []; decisions = []
    for item in planned:
        s, cfg = item['snapshot'], item['config']; p = s['season']['plan']
        assert p['state'] != 'unavailable', p['reason']
        config = {**cfg, '_stock_plan': p, '_season_reserved_ids': p['reserved_ids']}
        command_list = []; accepted = []
        groups = defaultdict(list)
        for f in s['fish']: groups[f['level'], f['grade']].append(f)
        batches = []
        for group in groups.values():
            for ordering in [group, sorted(group, key=lambda f: (f['remaining_breeds'], f['id']))]:
                batches += [ordering[i:i+10] for i in range(0, len(ordering)-9, 5)]
        for (level,grade), group in groups.items():
            adjacent = groups.get((level+1,grade), [])
            if len(group) >= 5 and len(adjacent) >= 5: batches.append(group[:5]+adjacent[:5])
        for batch in batches:
            command_list.append(dict(request_id=str(uuid.uuid4()), kind='merge', fish_ids=[f['id'] for f in batch]))
            accepted.append(not any(protected(f, config, s['captured_at'], action='merge') for f in batch)
                            and merge_allowed(s, config, batch))
        decision = decide(s, cfg, s['captured_at'])
        decisions.append(dict(kind=decision['kind'], phase=p['phase'], focus_species=p['focus_species'],
                              focus_grade=p['focus_grade'], stock_rows=len(p['stock']), focused_needs=len(p['needs'])))
        if decision['kind'] in ('breed','merge','craft'):
            command_list.append(dict(request_id=str(uuid.uuid4()), **{k:v for k,v in decision.items()
                                if k in ('kind','fish_ids','season','target_species','target_grade')}))
            accepted.append(True)
        item['commands'] = command_list; expected.append(accepted)
    guarded = native('guard', planned)
    for index, (item, wanted) in enumerate(zip(guarded, expected)):
        actual = [r['accepted'] for r in item['guards']]
        assert actual == wanted, (index, actual, wanted, item['guards'])
    summary = dict(cases=len(cases), guard_comparisons=sum(map(len, expected)), decisions=decisions,
                   scope='offline fixtures and optional archived snapshot; no live gameplay request')
    (out/'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, default=Path('dotnet'))
    parser.add_argument('--native-dll', type=Path, default=Path('tests/ModGuard/bin/Release/net8.0/ModGuard.dll'))
    parser.add_argument('--artifact-dir', type=Path, required=True)
    parser.add_argument('--archived-snapshot', type=Path)
    parser.add_argument('--species-table', type=Path)
    args = parser.parse_args()
    if args.archived_snapshot and not args.species_table: parser.error('--species-table is required with --archived-snapshot')
    run(args)
