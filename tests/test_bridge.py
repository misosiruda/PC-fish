from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

from pcfish_planner.bridge import decide, economic_plan, learning_records, protected_plan, recent_rejected_actions, recent_rejected_merges, recent_unpersisted_breed_pairs, validate_snapshot
from pcfish_planner.engine import ValidationError
from support import merge_example


def live_fish(id, *, level=3, grade=1, breeds=1, ready=0, **flags):
    return dict(id=id, species='FS00000' if level == 0 else f'species{level}', variant='variant', level=level,
                grade=0 if level == 0 else grade, remaining_breeds=breeds, growth=10, ready_at=ready,
                ready=ready == 0 and (level == 0 or breeds > 0), basic=level == 0, locked=False, seasonal=False, placed=False, **flags)


def snapshot(fish, **fields):
    return dict(session='test', revision='rev', config_digest='digest', captured_at=0, hearts=1, next_heart_at=420,
                capacity=999, ui_busy=False, request_in_flight=False, uncertain=False, fish=fish, **fields)


CONFIG = dict(enabled=True, mode='protected', auto_breed=True, auto_merge=True, protect_grade_at_least=3)


class BridgeTests(unittest.TestCase):
    def test_native_restore_retry_prioritizes_a_new_guarded_breed(self):
        s = snapshot([live_fish('a', level=0), live_fish('b', level=0)]
                     + [live_fish(f'm{i}', breeds=0, level=1) for i in range(10)],
                     breed_retry_after_server_restore=True)
        plan = decide(s, CONFIG, 0)
        self.assertEqual(plan['kind'], 'breed')
        self.assertEqual(set(plan['fish_ids']), {'a', 'b'})
        self.assertIn('restoration completed', plan['reason'])

    def test_native_restore_retry_keeps_existing_parent_protection(self):
        s = snapshot([live_fish('a', level=0), live_fish('b', level=0)],
                     breed_retry_after_server_restore=True)
        s['fish'][0]['locked'] = True
        self.assertNotEqual(decide(s, CONFIG, 0)['kind'], 'breed')

    def test_native_restore_retry_does_not_override_pause_or_pending(self):
        s = snapshot([live_fish('a', level=0), live_fish('b', level=0)],
                     breed_retry_after_server_restore=True)
        for field in ('uncertain', 'request_in_flight', 'ui_busy'):
            blocked = deepcopy(s); blocked[field] = True
            self.assertEqual(decide(blocked, CONFIG, 0)['kind'], 'hold')
        self.assertEqual(decide(s, {**CONFIG, 'enabled': False}, 0)['kind'], 'hold')

    def test_controlled_comparison_evidence_is_not_mixed_into_normal_learning(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = [Path(directory) / f'{n}.json' for n in range(3)]
            values = [{'event_id': 'legacy'}, {'event_id': 'regular', 'flow': 'native_breed'},
                      {'event_id': 'comparison', 'flow': 'native_comparison'}]
            for path, value in zip(paths, values):
                path.write_text(json.dumps(value), encoding='utf-8')
            self.assertEqual(learning_records(paths), values[:2])
            self.assertEqual(json.loads(paths[2].read_text(encoding='utf-8')), values[2])

    def test_unpersisted_pair_rotation_expires_and_later_success_clears(self):
        with tempfile.TemporaryDirectory() as directory:
            runtime = Path(directory); folder = runtime / 'deferred-receipts'; folder.mkdir()
            work = {'cmd': {'kind': 'breed', 'fish_ids': ['a', 'b']}, 'response_status': 'success',
                    'dispatched_at': 1000, 'absence_last_at': 1100, 'accounting_started': False, 'local_commit_started': False}
            (folder / 'new.json').write_text(json.dumps(work), encoding='utf-8')
            (folder / 'old.json').write_text(json.dumps({**work, 'dispatched_at': 0, 'absence_last_at': 1000, 'cmd': {'kind': 'breed', 'fish_ids': ['c', 'd']}}), encoding='utf-8')
            (runtime / 'events').mkdir()
            (runtime / 'events' / 'verified.json').write_text(json.dumps({'kind': 'breed', 'fish_ids': ['c', 'd'], 'completed_at': 1001, 'confirmed': True}), encoding='utf-8')
            self.assertEqual(recent_unpersisted_breed_pairs(runtime, 1599), [['a', 'b']])
            self.assertEqual(recent_unpersisted_breed_pairs(runtime, 1699), [['a', 'b']])
            self.assertEqual(recent_unpersisted_breed_pairs(runtime, 1700), [])
            s = snapshot([live_fish(id, level=0) for id in ('a', 'b', 'c', 'd')])
            s['hearts'] = 5
            plan = protected_plan(s, {**CONFIG, '_deferred_breed_pairs': recent_unpersisted_breed_pairs(runtime, 1599)})
            self.assertEqual(plan['kind'], 'breed')
            self.assertNotEqual(set(plan['fish_ids']), {'a', 'b'})

    def test_committed_or_invalid_deferred_receipts_do_not_block_pairs(self):
        with tempfile.TemporaryDirectory() as directory:
            runtime = Path(directory); folder = runtime / 'deferred-receipts'; folder.mkdir()
            work = {'cmd': {'kind': 'breed', 'fish_ids': ['a', 'b']}, 'response_status': 'success', 'dispatched_at': 1000}
            for i, change in enumerate([{'accounting_started': True}, {'local_commit_started': True},
                                        {'response_status': 'fail'}, {'dispatched_at': 2000},
                                        {'cmd': {'kind': 'merge', 'fish_ids': ['a', 'b']}}]):
                (folder / f'{i}.json').write_text(json.dumps({**work, **change}), encoding='utf-8')
            (folder / 'broken.json').write_text('{', encoding='utf-8')
            self.assertEqual(recent_unpersisted_breed_pairs(runtime, 1001), [])

    def test_remembered_menu_does_not_block_idle_aquarium_or_hide_real_block_reason(self):
        s = snapshot([live_fish('a', level=2), live_fish('b', level=2)])
        s.update(menu_name='Breed', window_open=False, home_menu=True, ui_busy=False, ui_block_reason='')
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'breed')
        s.update(ui_busy=True, ui_block_reason='Waiting for confirmation dialog')
        plan = decide(s, CONFIG, 0)
        self.assertEqual(plan['kind'], 'hold')
        self.assertEqual(plan['reason'], 'Waiting for confirmation dialog')

    def test_waits_for_better_future_pair_with_only_one_heart(self):
        s = snapshot([live_fish('a'), live_fish('b'), live_fish('c', level=4, ready=120), live_fish('d', level=5, ready=120)])
        plan = decide(s, CONFIG, 0)
        self.assertEqual((plan['kind'], plan['until']), ('wait', 120))
        self.assertIsNone(plan['profit_estimate'])

    def test_can_breed_now_without_losing_future_pair_when_hearts_sufficient(self):
        s = snapshot([live_fish('a'), live_fish('b'), live_fish('c', level=4, ready=120), live_fish('d', level=5, ready=120)])
        s['hearts'] = 2
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'breed')

    def test_ten_exhausted_materials_merge_before_breed(self):
        s = snapshot([live_fish(f'm{i}', level=1, breeds=0) for i in range(10)] + [live_fish('a'), live_fish('b')])
        plan = decide(s, CONFIG, 0)
        self.assertEqual(plan['kind'], 'merge')
        self.assertEqual(set(plan['fish_ids']), {f'm{i}' for i in range(10)})

    def test_full_hearts_breed_before_surplus_merge_and_future_better_parents(self):
        fish = [live_fish(f'm{i}', level=1, breeds=0) for i in range(10)]
        fish += [live_fish('a', level=1), live_fish('b', level=1),
                 live_fish('future-a', level=5, ready=120), live_fish('future-b', level=5, ready=120)]
        s = snapshot(fish); s['hearts'] = 5
        plan = decide(s, CONFIG, 0)
        self.assertEqual((plan['kind'], set(plan['fish_ids'])), ('breed', {'a', 'b'}))
        self.assertIn('Hearts full', plan['reason'])

    def test_full_hearts_still_allow_merge_when_breeding_unavailable(self):
        fish = [live_fish(f'm{i}', level=1, breeds=0) for i in range(10)]
        s = snapshot(fish); s['hearts'] = 5
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'merge')
        s['fish'] += [live_fish('a'), live_fish('b')]
        self.assertEqual(decide(s, {**CONFIG, 'auto_breed': False}, 0)['kind'], 'merge')
        s['capacity'] = len(s['fish'])
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'merge')

    def test_recent_merge_rejection_cannot_starve_ready_breeding_below_full_hearts(self):
        fish = [live_fish(f'm{i:02}', level=1, breeds=2) for i in range(20)]
        s = snapshot(fish); s['hearts'] = 4
        cfg = {**CONFIG, '_deferred_merge_batches': [[f['id'] for f in fish[:10]]]}
        self.assertEqual(decide(s, cfg, 0)['kind'], 'breed')
        # Other merges can still proceed when there is no immediately ready pair.
        for f in fish:
            f.update(ready_at=1000, ready=False)
        self.assertEqual(decide(s, cfg, 0)['kind'], 'merge')

    def test_rejected_breed_pair_does_not_hide_identical_alternative_parents(self):
        s = snapshot([live_fish(f'm{i}', level=1, breeds=2) for i in range(10)])
        cfg = {**CONFIG, 'auto_merge': False, '_deferred_breed_pairs': [['m0', 'm1']]}
        plan = decide(s, cfg, 0)
        self.assertEqual(plan['kind'], 'breed')
        self.assertNotEqual(set(plan['fish_ids']), {'m0', 'm1'})
        # Reject only the pair, rather than forbidding either parent globally.
        s['fish'] = s['fish'][:3]
        plan = decide(s, cfg, 0)
        self.assertEqual(plan['kind'], 'breed')
        self.assertIn('m2', plan['fish_ids'])
        self.assertTrue(set(plan['fish_ids']) & {'m0', 'm1'})

    def test_rejected_pairs_are_filtered_before_search_candidate_limit(self):
        from itertools import combinations
        fish = [live_fish(f'high{i}', level=4) for i in range(6)] + [live_fish('a', level=0), live_fish('b', level=0)]
        rejected = [[a['id'], b['id']] for a, b in combinations(fish, 2) if not (a['basic'] and b['basic'])]
        s = snapshot(fish); s['hearts'] = 5
        plan = decide(s, {**CONFIG, '_deferred_breed_pairs': rejected}, 0)
        self.assertEqual((plan['kind'], set(plan['fish_ids'])), ('breed', {'a', 'b'}))

    def test_breed_rejection_history_expires_and_requires_no_consumption(self):
        with tempfile.TemporaryDirectory() as directory:
            runtime = Path(directory); (runtime / 'rejected-events').mkdir()
            record = dict(kind='breed', outcome='rejected', consumed=False, completed_at=1000, cmd={'fish_ids': ['a', 'b']})
            path = runtime / 'rejected-events' / 'rejection.json'
            path.write_text(json.dumps(record), encoding='utf-8')
            self.assertEqual(recent_rejected_actions(runtime, 1599), {'merge': [], 'breed': [['a', 'b']]})
            self.assertEqual(recent_rejected_actions(runtime, 1600)['breed'], [])
            record['consumed'] = True
            path.write_text(json.dumps(record), encoding='utf-8')
            self.assertEqual(recent_rejected_actions(runtime, 1001)['breed'], [])

    def test_surplus_materials_can_merge_with_remaining_uses_or_cooldown(self):
        fish = [live_fish(f'm{i}', level=1, breeds=3, ready=120) for i in range(10)]
        self.assertEqual(decide(snapshot(fish), CONFIG, 0)['kind'], 'merge')
        starters = [live_fish(f'm{i}', level=0, breeds=0) for i in range(10)]
        self.assertNotEqual(decide(snapshot(starters), CONFIG, 0)['kind'], 'merge')

    def test_merge_uses_fewer_remaining_breeds_before_useful_surplus_parents(self):
        low = [live_fish(f'low{i}', level=1, breeds=i % 2) for i in range(10)]
        high = [live_fish(f'high{i}', level=1, breeds=5) for i in range(10)]
        plan = decide(snapshot(high + low), CONFIG, 0)
        self.assertEqual(plan['kind'], 'merge')
        self.assertEqual(set(plan['fish_ids']), {f['id'] for f in low})

    def test_rejected_batch_rotates_to_other_materials_without_protecting_individual_fish(self):
        fish = [live_fish(f'm{i:02}', level=1, breeds=2) for i in range(20)]
        rejected = [[f['id'] for f in fish[:10]]]
        cfg = {**CONFIG, 'auto_breed': False, '_deferred_merge_batches': rejected}
        plan = decide(snapshot(fish), cfg, 0)
        self.assertEqual(plan['kind'], 'merge')
        self.assertEqual(set(plan['fish_ids']), {f['id'] for f in fish[10:]})
        # With only eleven fish, a different combination can reuse nine of them.
        plan = decide(snapshot(fish[:11]), cfg, 0)
        self.assertEqual(plan['kind'], 'merge')
        self.assertNotEqual(set(plan['fish_ids']), set(rejected[0]))

    def test_rejected_merge_does_not_block_breeding_and_expires_after_ten_minutes(self):
        fish = [live_fish(f'm{i}', level=1, breeds=2) for i in range(10)]
        ids = [f['id'] for f in fish]
        self.assertEqual(decide(snapshot(fish), {**CONFIG, '_deferred_merge_batches': [ids]}, 0)['kind'], 'breed')
        with tempfile.TemporaryDirectory() as directory:
            runtime = Path(directory)
            (runtime / 'rejected-events').mkdir()
            record = dict(kind='merge', outcome='rejected', consumed=False, completed_at=1000, cmd={'fish_ids': ids})
            path = runtime / 'rejected-events' / 'rejection.json'
            path.write_text(json.dumps(record), encoding='utf-8')
            self.assertEqual(recent_rejected_merges(runtime, 1599), [ids])
            self.assertEqual(recent_rejected_merges(runtime, 1600), [])
            record['consumed'] = True
            path.write_text(json.dumps(record), encoding='utf-8')
            self.assertEqual(recent_rejected_merges(runtime, 1001), [])
            path.write_text('{partial', encoding='utf-8')
            self.assertEqual(recent_rejected_merges(runtime, 1001), [])

    def test_grade_season_locked_reserved_materials_are_protected(self):
        for flags in [dict(grade=3), dict(grade=5), dict(seasonal=True), dict(locked=True), dict(reserved=True)]:
            fish = [live_fish(f'm{i}', level=1, breeds=0) for i in range(10)]
            cfg = deepcopy(CONFIG)
            if flags.get('reserved'):
                cfg['reserved_ids'] = ['m0']
            else:
                fish[0].update(flags)
            self.assertNotEqual(decide(snapshot(fish), cfg, 0)['kind'], 'merge')

    def test_protection_applies_to_parent_usage_too(self):
        for flags in [dict(grade=3), dict(seasonal=True), dict(locked=True)]:
            fish = [live_fish('a'), live_fish('b')]
            fish[0].update(flags)
            self.assertEqual(decide(snapshot(fish), CONFIG, 0)['kind'], 'hold')

    def test_disabled_observe_stale_busy_or_uncertain_never_dispatches(self):
        base = snapshot([live_fish('a'), live_fish('b')])
        for field in ('ui_busy', 'request_in_flight', 'uncertain'):
            s = deepcopy(base)
            s[field] = True
            self.assertEqual(decide(s, CONFIG, 0)['kind'], 'hold')
        self.assertEqual(decide(base, CONFIG, 10)['kind'], 'hold')
        for cfg in ({**CONFIG, 'enabled': False}, {**CONFIG, 'mode': 'observe'}):
            self.assertEqual(decide(base, cfg, 0)['kind'], 'hold')

    def test_full_inventory_stops_breed_but_allows_merge(self):
        s = snapshot([live_fish('a'), live_fish('b')])
        s['capacity'] = 2
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'hold')
        s = snapshot([live_fish(str(i), level=1, breeds=0) for i in range(10)])
        s['capacity'] = 10
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'merge')

    def test_invalid_snapshot_fails_before_any_recommendation(self):
        s = snapshot([live_fish('a'), live_fish('a')])
        with self.assertRaises(ValidationError):
            decide(s, CONFIG, 0)
        s['fish'][1]['id'] = 'b'
        s['fish'][1]['level'] = 6
        with self.assertRaises(ValidationError):
            decide(s, CONFIG, 0)

    def test_economic_mode_missing_data_holds_without_fallback(self):
        s = snapshot([live_fish('a'), live_fish('b')])
        self.assertEqual(decide(s, {**CONFIG, 'mode': 'economic'}, 0)['kind'], 'hold')

    def test_economic_bridge_uses_verified_joint_planner_and_rejects_synthetic(self):
        model = merge_example()
        s = snapshot([live_fish(f'material{i}', level=1, breeds=0) for i in range(10)])
        for f in s['fish']:
            f['species'] = 'material'
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / 'model.json'
            cfg = {**CONFIG, 'mode': 'economic', 'economic_model_file': str(path)}
            path.write_text(json.dumps(model))
            with self.assertRaises(ValidationError):
                economic_plan(s, cfg)
            model['synthetic'] = False
            for r in model['rules']:
                r['evidence'] = 'verified'
            for q in model['quotes']:
                q['evidence'] = 'verified'
            path.write_text(json.dumps(model))
            self.assertEqual(economic_plan(s, cfg)['kind'], 'merge')
            # Missing a single result quote must hold, not switch to protected mode.
            model['quotes'].pop()
            path.write_text(json.dumps(model))
            self.assertEqual(economic_plan(s, cfg)['kind'], 'hold')

    def test_only_currently_mature_starters_are_used(self):
        fish = [live_fish('a', level=0, breeds=0), live_fish('b', level=0, breeds=0)]
        self.assertEqual(decide(snapshot(fish), CONFIG, 0)['kind'], 'breed')
        fish[1]['ready'] = False
        fish[1]['growth'] = 2
        self.assertEqual(decide(snapshot(fish), CONFIG, 0)['kind'], 'hold')
