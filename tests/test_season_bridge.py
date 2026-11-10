from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from pcfish_planner.bridge import decide, run
from test_bridge import CONFIG, live_fish, snapshot
from support import fish, problem, quote, result, rule, merge_example


class SeasonBridgeTests(unittest.TestCase):
    def state(self):
        s = snapshot([live_fish('a'), live_fish('b')])
        s['season'] = {'id': 2, 'plan': {'state': 'waiting', 'reason': 'Saving ingredients',
                                      'reserved_ids': ['a'], 'needs': [], 'next_craft': None}}
        return s

    def test_reserved_material_can_breed_without_overriding_manual_reservations(self):
        s = self.state()
        cfg = {**CONFIG, 'auto_season_craft': True}
        plan = decide(s, cfg, 0)
        self.assertEqual(plan['kind'], 'breed')
        self.assertIn('a', plan['fish_ids'])
        self.assertEqual(decide(s, {**cfg, 'reserved_ids': ['a']}, 0)['kind'], 'hold')
        self.assertEqual(decide(s, CONFIG, 0)['kind'], 'breed')
        self.assertNotIn('reserved_ids', cfg)  # Derived reservations never rewrite user settings.

    def test_season_reservation_does_not_override_other_breed_protections(self):
        for condition in ('locked', 'seasonal', 'grade', 'manual_id', 'manual_species', 'pending', 'keep', 'expired', 'cooldown', 'exhausted'):
            with self.subTest(condition=condition):
                s = self.state()
                cfg = {**CONFIG, 'auto_season_craft': True}
                f = s['fish'][0]
                if condition in ('locked', 'seasonal'):
                    f[condition] = True
                elif condition == 'grade':
                    f['grade'] = 3
                elif condition == 'manual_id':
                    cfg['reserved_ids'] = ['a']
                elif condition == 'manual_species':
                    cfg['reserved_species'] = [f['species']]
                elif condition in ('pending', 'keep', 'expired'):
                    cfg['protection_mode'] = 'market'
                    for item in s['fish']:
                        item.update(market_state='release', market_valid_until=100)
                    f.update(market_state=condition if condition != 'expired' else 'release',
                             market_valid_until=0 if condition == 'expired' else 100)
                elif condition == 'cooldown':
                    f.update(ready=False, ready_at=1000)
                elif condition == 'exhausted':
                    f.update(ready=False, remaining_breeds=0)
                self.assertNotEqual(decide(s, cfg, 0)['kind'], 'breed')

    def test_reserved_material_cannot_be_merged(self):
        s = self.state()
        s['fish'] = [live_fish('a' if i == 0 else str(i), level=1, breeds=0) for i in range(10)]
        self.assertNotEqual(decide(s, {**CONFIG, 'auto_season_craft': True}, 0)['kind'], 'merge')
        s['season']['plan']['reserved_ids'] = []
        self.assertEqual(decide(s, {**CONFIG, 'auto_season_craft': True}, 0)['kind'], 'merge')

    def test_craft_precedes_regular_actions_and_carries_recipe_identity(self):
        s = self.state()
        s['season']['plan'].update(state='ready', next_craft={'season': 2, 'species': 'season-fish', 'grade': 1, 'fish_ids': ['a']})
        for mode in ('protected', 'economic'):
            plan = decide(s, {**CONFIG, 'mode': mode, 'auto_season_craft': True}, 0)
            self.assertEqual((plan['kind'], plan['season'], plan['target_species'], plan['target_grade'], plan['fish_ids']),
                             ('craft', 2, 'season-fish', 1, ['a']))
        for flag in ('ui_busy', 'uncertain', 'request_in_flight'):
            state = deepcopy(s)
            state[flag] = True
            self.assertEqual(decide(state, {**CONFIG, 'auto_season_craft': True}, 0)['kind'], 'hold')

    def test_missing_or_unavailable_game_recipes_hold_consumption(self):
        for season in (None, {}, {'plan': {'state': 'unavailable', 'reason': 'Recipe table missing'}}):
            s = self.state()
            s['season'] = season
            self.assertEqual(decide(s, {**CONFIG, 'auto_season_craft': True}, 0)['kind'], 'hold')

    def test_material_reservations_reach_economic_adapter(self):
        s = self.state()
        with patch('pcfish_planner.bridge.economic_plan', return_value={'kind': 'hold'}) as economic:
            decide(s, {**CONFIG, 'mode': 'economic', 'auto_season_craft': True, 'reserved_ids': ['manual']}, 0)
            self.assertEqual(economic.call_args.args[1]['reserved_ids'], ['manual'])
            self.assertEqual(economic.call_args.args[1]['_season_reserved_ids'], ['a'])

    def test_focused_craft_precedes_post_restore_breeding_retry(self):
        s=self.state()
        s['breed_retry_after_server_restore']=True
        s['season']['plan'].update(state='ready',focus_species='FS00040',
            next_craft={'season':2,'species':'FS00040','grade':1,'fish_ids':['a']})
        cfg={**CONFIG,'auto_season_craft':True}
        self.assertEqual(decide(s,cfg,0)['kind'],'craft')
        s['season']['plan'].update(state='waiting',next_craft=None)
        plan=decide(s,cfg,0)
        self.assertEqual(plan['kind'],'breed')
        self.assertEqual(plan['season_details']['focus_species'],'FS00040')

    def test_economic_engine_can_breed_reserved_material_but_cannot_merge_it(self):
        # The fixture is isolated test data; no live model file is written.
        parents = [fish('a'), fish('b')]
        model = problem(parents, [rule('breed-test', parents, [(1, result())])],
                        [quote('species3', breeds=1, value=1), quote('species3', value=1), quote('child', value=40)])
        cfg = {**CONFIG, 'mode': 'economic', 'auto_season_craft': True, 'economic_model_file': 'isolated-test-model'}

        def verified_fixture(payload):
            payload['synthetic'] = False
            for item in payload['rules']:
                item.update(evidence='verified', sample_count=30)
            for item in payload['quotes']:
                item['evidence'] = 'verified'
            return payload

        with patch('pcfish_planner.bridge.read_json', return_value=verified_fixture(model)):
            plan = decide(self.state(), cfg, 0)
            self.assertEqual(plan['kind'], 'breed')
            self.assertEqual(set(plan['fish_ids']), {'a', 'b'})
            self.assertNotEqual(decide(self.state(), {**cfg, 'reserved_ids': ['a']}, 0)['kind'], 'breed')

        model = verified_fixture(merge_example())
        s = self.state()
        s['fish'] = [live_fish(f'material{i}', level=1, breeds=0) for i in range(10)]
        for item in s['fish']:
            item['species'] = 'material'
        s['season']['plan']['reserved_ids'] = ['material0']
        with patch('pcfish_planner.bridge.read_json', return_value=model):
            self.assertNotEqual(decide(s, cfg, 0)['kind'], 'merge')
            s['season']['plan']['reserved_ids'] = []
            self.assertEqual(decide(s, cfg, 0)['kind'], 'merge')

    def test_worker_writes_craft_command_and_keeps_craft_events_out_of_learning(self):
        from hashlib import sha256
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            cfg = {**CONFIG, 'auto_season_craft': True}
            raw = json.dumps(cfg)
            (root / 'config.json').write_text(raw)
            s = self.state()
            s['config_digest'] = sha256(raw.encode()).hexdigest()
            s['season']['plan'].update(state='ready', next_craft={'season': 2, 'species': 'season-fish', 'grade': 1, 'fish_ids': ['a']})
            (root / 'snapshot.json').write_text(json.dumps(s))
            (root / 'craft-events').mkdir()
            (root / 'craft-events' / 'confirmed.json').write_text('{"kind":"craft","confirmed":true}')
            with patch('pcfish_planner.bridge.time.time', return_value=0), patch('pcfish_planner.bridge.time.sleep', side_effect=KeyboardInterrupt):
                with self.assertRaises(KeyboardInterrupt):
                    run(root, root / 'config.json', 'test')
            command = json.loads((root / 'command.json').read_text())
            self.assertEqual((command['kind'], command['season'], command['target_species'], command['target_grade']),
                             ('craft', 2, 'season-fish', 1))
            self.assertEqual(command['config_digest'], s['config_digest'])
            self.assertTrue((root / 'season-review.json').exists())
            self.assertFalse((root / 'observed-rules.json').exists())


if __name__ == '__main__':
    unittest.main()
