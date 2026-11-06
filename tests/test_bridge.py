from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

from pcfish_planner.bridge import decide, economic_plan, protected_plan, validate_snapshot
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

    def test_cooldown_and_starters_never_count_as_exhaustion(self):
        for fish in ([live_fish(f'm{i}', level=1, ready=120) for i in range(10)],
                     [live_fish(f'm{i}', level=0, breeds=0) for i in range(10)]):
            plan = decide(snapshot(fish), CONFIG, 0)
            self.assertNotEqual(plan['kind'], 'merge')

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
