from copy import deepcopy
import unittest
from unittest.mock import patch

from pcfish_planner.bridge import decide, economic_plan
from pcfish_planner.engine import ValidationError
from pcfish_planner.stock_policy import merge_allowed, validate_plan
from test_bridge import CONFIG, live_fish, snapshot
from support import fish, problem, quote, result, rule


def need(level, species='wanted', grade=1):
    return dict(species=species, level=level, grade=grade, required=3, reserved=0, missing=3)


def policy_state(fishes, needs=(), reserved=(), surplus=()):
    plan = dict(policy_version=2, state='waiting', phase='collection', reason='Gather focused material',
                focus_species='jellyfish', focus_grade=5, reserved_ids=list(reserved),
                exhausted_ids=[f['id'] for f in fishes if f['remaining_breeds'] == 0 and f['id'] not in reserved],
                surplus_ids=list(surplus), parent_ids=[], needs=list(needs), stock=[], next_craft=None)
    return snapshot(fishes, season=dict(id=2, plan=plan))


CFG = {**CONFIG, 'auto_season_craft': True, 'protect_grade_at_least': 5}


class StockPolicyTests(unittest.TestCase):
    def test_needed_low_rarity_beats_highest_parents(self):
        fishes = [live_fish('low-a', level=0), live_fish('low-b', level=0),
                  live_fish('high-a', level=5), live_fish('high-b', level=5)]
        plan = decide(policy_state(fishes, [need(1)]), CFG, 0)
        self.assertEqual((plan['kind'], set(plan['fish_ids'])), ('breed', {'low-a', 'low-b'}))
        self.assertEqual(plan['objective'], 'material_rarity_progress')
        self.assertIsNone(plan['profit_estimate'])

    def test_low_parents_survive_large_high_rarity_pool(self):
        fishes = [live_fish(f'high{i}', level=5, breeds=i+1, ready=i) for i in range(30)]
        fishes += [live_fish('starter-a', level=0), live_fish('starter-b', level=0)]
        plan = decide(policy_state(fishes, [need(1)]), CFG, 0)
        self.assertEqual(set(plan['fish_ids']), {'starter-a', 'starter-b'})

    def test_wait_for_useful_parents_instead_of_irrelevant_breed(self):
        fishes = [live_fish('low-a', level=1, ready=60), live_fish('low-b', level=1, ready=60),
                  live_fish('high-a', level=5), live_fish('high-b', level=5)]
        plan = decide(policy_state(fishes, [need(1)]), CFG, 0)
        self.assertEqual((plan['kind'], plan['until']), ('wait', 60))

    def test_no_material_need_does_not_invent_breeding_progress(self):
        s = policy_state([live_fish('a'), live_fish('b')])
        self.assertEqual(decide(s, CFG, 0)['kind'], 'hold')

    def test_targeted_merge_only_if_forecast_can_help_shortage(self):
        fishes = [live_fish(str(i), level=1, breeds=0) for i in range(10)]
        s = policy_state(fishes, [need(2)])
        self.assertEqual(decide(s, CFG, 0)['kind'], 'merge')
        s['season']['plan']['needs'] = [need(5)]
        self.assertEqual(decide(s, CFG, 0)['kind'], 'hold')

    def test_targeted_adjacent_batch_precedes_unrelated_surplus(self):
        low = [live_fish('low' + str(i), level=1, breeds=0) for i in range(9)] + [live_fish('mid', level=2, breeds=0)]
        extra = [live_fish('extra' + str(i), level=4, breeds=0) for i in range(10)]
        s = policy_state(low + extra, [need(3)], surplus=[f['id'] for f in extra])
        self.assertEqual(set(decide(s, CFG, 0)['fish_ids']), {f['id'] for f in low})

    def test_parent_rejection_does_not_hide_unrejected_pair(self):
        fishes = [live_fish(str(i), level=1) for i in range(8)]
        rejected = [[str(a), str(b)] for a in range(6) for b in range(a+1, 6)]
        s = policy_state(fishes, [need(1)])
        result = decide(s, {**CFG, '_deferred_breed_pairs': rejected}, 0)
        self.assertEqual(result['kind'], 'breed')
        self.assertTrue({'6', '7'} & set(result['fish_ids']))

    def test_positive_uses_require_surplus_allocation(self):
        fishes = [live_fish(str(i), level=1, breeds=2) for i in range(10)]
        s = policy_state(fishes, [need(2)])
        self.assertEqual(decide(s, {**CFG, 'auto_breed': False}, 0)['kind'], 'hold')
        s['season']['plan']['surplus_ids'] = [f['id'] for f in fishes]
        self.assertEqual(decide(s, {**CFG, 'auto_breed': False}, 0)['kind'], 'merge')

    def test_surplus_never_mixes_rarities_or_stars(self):
        for field in ('level', 'grade'):
            fishes = [live_fish(str(i), level=1, breeds=2) for i in range(10)]
            fishes[-1][field] = 2
            s = policy_state(fishes, surplus=[f['id'] for f in fishes])
            self.assertEqual(decide(s, {**CFG, 'auto_breed': False}, 0)['kind'], 'hold')

    def test_surplus_batch_cannot_skip_exhausted_materials(self):
        fishes = [live_fish(str(i), level=1, breeds=0 if i < 10 else 3) for i in range(20)]
        s = policy_state(fishes, surplus=[f['id'] for f in fishes])
        config = {**CFG, 'auto_breed': False, '_stock_plan': s['season']['plan']}
        self.assertFalse(merge_allowed(s, config, fishes[10:]))
        self.assertEqual(set(decide(s, config, 0)['fish_ids']), {f['id'] for f in fishes[:10]})

    def test_grade_five_nine_total_cannot_form_batch_after_reserve(self):
        fishes = [live_fish(str(i), level=1, grade=5, breeds=0) for i in range(9)]
        s = policy_state(fishes, reserved=['0', '1', '2'], surplus=[str(i) for i in range(3, 9)])
        for f in fishes:
            f.update(market_state='release', market_merge_state='release', market_valid_until=100, market_merge_valid_until=100)
        cfg = {**CFG, 'protection_mode': 'market', 'market_merge_price_basis': 'highest_buy_order'}
        self.assertEqual(decide(s, cfg, 0)['kind'], 'hold')

    def test_old_plugin_is_held_and_malformed_allocations_rejected(self):
        s = policy_state([live_fish('a'), live_fish('b')], [need(3)])
        del s['season']['plan']['policy_version']
        self.assertEqual(decide(s, CFG, 0)['kind'], 'hold')
        for change in ('unknown', 'overlap', 'duplicate', 'bad_need'):
            s = policy_state([live_fish('a'), live_fish('b')], [need(3)])
            p = s['season']['plan']
            if change == 'unknown': p['reserved_ids'] = ['missing']
            elif change == 'overlap': p.update(reserved_ids=['a'], surplus_ids=['a'])
            elif change == 'duplicate': p['surplus_ids'] = ['a', 'a']
            else: p['needs'][0]['missing'] = -1
            with self.subTest(change=change), self.assertRaises(ValidationError):
                validate_plan(p, s['fish'])

    def test_economic_models_target_material_before_expensive_wrong_fish(self):
        parents = [fish('a'), fish('b'), fish('c', level=4), fish('d', level=4)]
        model = problem(parents, [rule('useful', parents[:2], [(1, result('wanted', level=2))]),
                                  rule('expensive', parents[2:], [(1, result('valuable', level=5))])],
                        [quote('species3', breeds=n, value=10) for n in (0, 1)]
                        + [quote('species4', breeds=n, value=10) for n in (0, 1)]
                        + [quote('wanted', value=1), quote('valuable', value=1000)], max_parent_fish=2, minimum_gain=100)
        model['synthetic'] = False
        for r in model['rules']: r.update(evidence='verified', sample_count=30)
        for q in model['quotes']: q['evidence'] = 'verified'
        s = policy_state([live_fish('a'), live_fish('b'), live_fish('c', level=4), live_fish('d', level=4)], [need(2)])
        with patch('pcfish_planner.bridge.read_json', return_value=model):
            plan = decide(s, {**CFG, 'mode': 'economic', 'economic_model_file': 'test-only'}, 0)
        self.assertEqual((plan['kind'], set(plan['fish_ids'])), ('breed', {'a', 'b'}))
        self.assertNotIn('expected_inventory_value_gain', plan['economic_details'])
        self.assertGreater(plan['economic_details']['expected_material_gain'], 0)

    def test_economic_lookahead_can_exhaust_parents_then_merge_without_stock(self):
        parents = [fish('a', level=1), fish('b', level=1)]
        exhausted = [fish(str(i), level=1, breeds=0) for i in range(8)]
        merge_inputs = [dict(f, remaining_breeds=0) for f in parents] + exhausted
        model = problem(parents + exhausted,
                        [rule('breed', parents, [(1, result('child', level=1))]),
                         rule('merge', merge_inputs, [(1, result('merged', level=2))], kind='merge')],
                        [quote('species1', breeds=1, value=10), quote('species1', value=1),
                         quote('child', value=0), quote('merged', value=50)], max_actions=2)
        model['synthetic'] = False
        for r in model['rules']: r.update(evidence='verified', sample_count=30)
        for q in model['quotes']: q['evidence'] = 'verified'
        fishes = [live_fish(f['id'], level=1, breeds=f['remaining_breeds']) for f in parents + exhausted]
        cfg = {**CONFIG, 'mode': 'economic', 'economic_model_file': 'test-only', 'auto_season_craft': False}
        with patch('pcfish_planner.bridge.read_json', return_value=deepcopy(model)):
            plan = decide(snapshot(fishes), cfg, 0)
        self.assertEqual((plan['kind'], set(plan['fish_ids'])), ('breed', {'a', 'b'}))
        self.assertLess(plan['economic_details']['immediate_gain'], 0)
        self.assertGreater(plan['economic_details']['expected_inventory_value_gain'], 0)
        # A stock allocation still requires a real result before planning a merge.
        with patch('pcfish_planner.bridge.read_json', return_value=deepcopy(model)):
            plan = decide(policy_state(fishes, [need(2, species='merged')]), {**cfg, 'auto_season_craft': True}, 0)
        self.assertEqual(plan['kind'], 'hold')

    def test_economic_without_stock_cannot_merge_unexhausted_fish(self):
        fishes = [fish(str(i), level=1) for i in range(10)]
        model = problem(fishes, [rule('merge', fishes, [(1, result('merged', level=2))], kind='merge')],
                        [quote('species1', breeds=1, value=1), quote('merged', value=100)])
        model['synthetic'] = False
        for r in model['rules']: r.update(evidence='verified', sample_count=30)
        for q in model['quotes']: q['evidence'] = 'verified'
        with patch('pcfish_planner.bridge.read_json', return_value=model):
            plan = decide(snapshot([live_fish(f['id'], level=1) for f in fishes]),
                          {**CONFIG, 'mode': 'economic', 'economic_model_file': 'test-only', 'auto_breed': False}, 0)
        self.assertEqual(plan['kind'], 'hold')


if __name__ == '__main__':
    unittest.main()
