from copy import deepcopy
from dataclasses import replace
import unittest

from pcfish_planner.engine import Action, Fish, Planner, Problem, ValidationError
from pcfish_planner.probabilities import breed_level_probabilities, merge_level_probabilities
from tests.support import fish, merge_example, problem, quote, result, rule, wait_example


class PlanningTests(unittest.TestCase):
    def run_plan(self, payload):
        return Planner(Problem(payload)).run()

    def test_waits_for_better_pair_instead_of_spending_only_heart(self):
        data = wait_example()
        before = deepcopy(data)
        plan = self.run_plan(data)
        self.assertEqual(plan['action']['kind'], 'wait')
        self.assertEqual(plan['action']['until'], 120)
        self.assertAlmostEqual(plan['expected_inventory_value_gain'], 44)
        self.assertEqual(data, before)
        self.assertFalse(plan['execution_supported'])

    def test_no_reason_to_wait_if_future_pair_cannot_arrive_in_horizon(self):
        data = wait_example()
        data['config']['horizon_seconds'] = 100
        self.assertEqual(self.run_plan(data)['action']['kind'], 'breed')

    def test_probabilities_are_expected_values_not_best_possible_outcome(self):
        parents = [fish('a'), fish('b')]
        data = problem(parents, [rule('uncertain', parents, [(.25, result('good')), (.75, result('ordinary'))])],
                       [quote('species3', breeds=1), quote('species3'), quote('good', value=100), quote('ordinary', value=20)])
        self.assertAlmostEqual(self.run_plan(data)['expected_inventory_value_gain'], 40)

    def test_parent_resale_value_loss_can_make_breeding_unprofitable(self):
        parents = [fish('a', breeds=2), fish('b', breeds=2)]
        data = problem(parents, [rule('loss', parents, [(1, result())])],
                       [quote('species3', breeds=2, value=1000), quote('species3', breeds=1, value=100), quote('child', value=300)])
        self.assertEqual(self.run_plan(data)['action']['kind'], 'hold')

    def test_merges_ten_low_value_exhausted_fish(self):
        plan = self.run_plan(merge_example())
        self.assertEqual(plan['action']['kind'], 'merge')
        self.assertEqual(len(set(plan['action']['fish_ids'])), 10)
        self.assertEqual(plan['expected_inventory_value_gain'], 30)

    def test_grade_five_is_merge_protected_by_default(self):
        plan = self.run_plan(merge_example(True))
        self.assertEqual(plan['action']['kind'], 'hold')
        self.assertEqual(len(plan['merge_protected_fish_ids']), 10)

    def test_market_opportunity_cost_also_protects_expensive_materials(self):
        data = merge_example(True)
        data['config']['protect_merge_grade_at_least'] = 6
        plan = self.run_plan(data)
        self.assertEqual(plan['action']['kind'], 'hold')
        self.assertEqual(plan['alternatives'][0]['immediate_gain'], -8500)

    def test_changed_remaining_breeds_still_require_matching_outcome_model(self):
        data = merge_example()
        data['state']['fish'][0]['remaining_breeds'] = 1
        data['state']['fish'][0]['ready_at'] = 200
        self.assertNotEqual(self.run_plan(data)['action']['kind'], 'merge')

    def test_nonexhausted_materials_can_merge_with_correct_model_and_opportunity_cost(self):
        data = merge_example()
        for f in data['state']['fish']:
            f.update(remaining_breeds=3, ready_at=200)
        for f in data['rules'][0]['parents']:
            f['remaining_breeds'] = 3
        data['quotes'][0]['remaining_breeds'] = 3
        plan = self.run_plan(data)
        self.assertEqual(plan['action']['kind'], 'merge')
        self.assertEqual(plan['expected_inventory_value_gain'], 30)

    def test_locked_for_merge_does_not_block_breeding(self):
        data = wait_example()
        data['config']['horizon_seconds'] = 100
        data['state']['fish'][0]['protected_merge'] = True
        self.assertEqual(self.run_plan(data)['action']['kind'], 'breed')

    def test_seasonal_and_reserved_materials_are_preserved(self):
        for field in ['seasonal', 'reserved', 'protected_merge']:
            with self.subTest(field=field):
                data = merge_example()
                data['state']['fish'][0][field] = True
                self.assertNotEqual(self.run_plan(data)['action']['kind'], 'merge')

    def test_missing_model_is_reported_without_inventing_grade_distribution(self):
        data = wait_example()
        data['rules'] = []
        plan = self.run_plan(data)
        self.assertEqual(plan['status'], 'hold_missing_data')
        self.assertTrue(any('joint_outcome_model_missing' in x['issue'] for x in plan['data_requirements']))

    def test_unknown_merge_model_is_reported(self):
        data = merge_example()
        data['rules'] = []
        self.assertIn('joint_outcome_model_missing:merge', str(self.run_plan(data)['data_requirements']))

    def test_one_unpriced_outcome_blocks_whole_distribution(self):
        data = wait_example()
        data['config']['horizon_seconds'] = 100
        data['quotes'] = [q for q in data['quotes'] if q['species'] != 'child5']
        plan = self.run_plan(data)
        self.assertEqual(plan['action']['kind'], 'hold')
        self.assertIn('quote_missing:child5', str(plan['data_requirements']))

    def test_unknown_unchanged_inventory_does_not_block_known_profitable_action(self):
        data = merge_example()
        data['state']['fish'].append(fish('unknown', protected_breed=True))
        self.assertEqual(self.run_plan(data)['action']['kind'], 'merge')

    def test_assumptions_are_disabled_unless_explicitly_allowed(self):
        data = wait_example()
        data['config']['allow_assumptions'] = False
        plan = self.run_plan(data)
        self.assertEqual(plan['status'], 'hold_missing_data')
        self.assertEqual(plan['action']['kind'], 'hold')

    def test_not_enough_observations_blocks_learned_model(self):
        data = wait_example()
        for model in data['rules']:
            model.update(evidence='observed', sample_count=2)
        self.assertEqual(self.run_plan(data)['action']['kind'], 'hold')

    def test_stale_quotes_are_not_used(self):
        data = wait_example()
        data['state']['now'] = 100000
        self.assertIn('quote_stale', str(self.run_plan(data)['data_requirements']))

    def test_price_liquidity_input_is_applied(self):
        data = merge_example()
        data['quotes'][-1]['sale_probability'] = .5
        self.assertEqual(self.run_plan(data)['expected_inventory_value_gain'], 10)

    def test_both_in_flight_and_ambiguous_last_result_interlock(self):
        for field in ['request_in_flight', 'last_result_uncertain']:
            data = merge_example()
            data['state'][field] = True
            plan = self.run_plan(data)
            self.assertEqual(plan['action']['kind'], 'hold')
            self.assertEqual(plan['status'], 'hold_pending_reconciliation')
            self.assertEqual(plan['search']['nodes'], 0)

    def test_starter_zero_is_unlimited_only_when_explicit(self):
        starter = Fish.read(fish('starter', level=0, grade=0, breeds=0, unlimited_breeding=True))
        self.assertTrue(starter.can_breed(0))
        self.assertFalse(Planner(Problem(problem([dict(starter.__dict__)]))).merge_eligible(starter))
        self.assertFalse(replace(starter, unlimited_breeding=False).can_breed(0))

    def test_starter_growth_recovery_needs_its_own_timing_model(self):
        parents = [fish(id, level=0, grade=0, breeds=0, unlimited_breeding=True) for id in ['a', 'b']]
        data = problem(parents, [rule('starter', parents, [(1, result('common', level=1))])], [quote('common', value=10)])
        plan = self.run_plan(data)
        self.assertEqual(plan['status'], 'hold_missing_data')
        self.assertIn('starter_growth_recovery_model_missing', str(plan['data_requirements']))
        for f in data['state']['fish']:
            f['breed_recovery_seconds'] = 60
        self.assertEqual(self.run_plan(data)['action']['kind'], 'breed')

    def test_grade_and_level_are_distinct_model_inputs(self):
        data = wait_example()
        for f in data['state']['fish']:
            f['grade'] = 2
        self.assertEqual(self.run_plan(data)['action']['kind'], 'hold')

    def test_expected_value_of_sequential_actions_is_not_double_counted(self):
        data = merge_example()
        data['state']['fish'] += [fish(f'extra{i}', level=1, species='material', breeds=0) for i in range(10)]
        data['config']['max_actions'] = 2
        self.assertEqual(self.run_plan(data)['expected_inventory_value_gain'], 60)

    def test_plans_merge_before_breed_to_obtain_better_parent(self):
        data = merge_example()
        a, b = fish('a'), fish('b')
        merged = fish('m', level=2, species='merged')
        data['state']['fish'] += [a, b]
        data['rules'][0]['outcomes'][0]['fish']['ready_after_seconds'] = 0
        data['rules'] += [rule('ordinary', [a, b], [(1, result('ordinary', level=3))]),
                          rule('better', [a, merged], [(1, result('valuable', level=4))])]
        data['quotes'] += [quote('species3', breeds=1, value=10), quote('species3', value=10),
                           quote('merged', value=40), quote('ordinary', value=2), quote('valuable', value=100)]
        data['state']['hearts'] = 2
        data['config']['max_actions'] = 2
        plan = self.run_plan(data)
        self.assertEqual(plan['action']['kind'], 'merge')
        self.assertEqual(plan['expected_inventory_value_gain'], 130)

    def test_same_immediate_reward_can_preserve_parent_for_future_pair(self):
        a, b = fish('a', level=4), fish('b', level=4)
        c, d = fish('c', level=3), fish('d', level=5)
        future = fish('e', level=4, species='future4', ready=120)
        models = [rule('four-four', [a, b], [(1, result('small', level=3))]),
                  rule('three-five', [c, d], [(1, result('small', level=3))]),
                  rule('future-five', [future, d], [(1, result('big', level=5))])]
        quotes = [quote(species, breeds=n, value=10) for species in ['species3', 'species4', 'species5', 'future4'] for n in [0, 1]]
        data = problem([a, b, c, d, future], models, quotes + [quote('small', value=5), quote('big', value=100)], max_actions=2)
        data['state']['hearts'] = 2
        plan = self.run_plan(data)
        self.assertEqual(plan['action']['fish_ids'], ('a', 'b'))
        self.assertEqual(plan['expected_inventory_value_gain'], 105)

    def test_result_readiness_and_parent_cooldown_are_retained(self):
        data = wait_example()
        engine = Planner(Problem(data))
        state = engine.p.state
        paths = engine.transitions(state, Action('breed', ('low-a', 'low-b')))
        for _, _, after in paths:
            self.assertEqual(next(f for f in after.fish if f.id == 'low-a').ready_at, 181)
            self.assertEqual(next(f for f in after.fish if f.id.startswith('planned:')).ready_at, 421)

    def test_bounded_search_reports_truncation(self):
        data = wait_example()
        data['config']['max_nodes'] = 1
        plan = self.run_plan(data)
        self.assertIn('node_budget_reached', plan['search']['limits'])
        self.assertFalse(plan['search']['globally_optimal'])


class ClockTests(unittest.TestCase):
    def engine(self, hearts, now, due):
        data = problem([])
        data['state'].update(hearts=hearts, now=now, next_heart_at=due)
        return Planner(Problem(data))

    def test_capped_clock_retains_due_time_without_banking_many_hearts(self):
        engine = self.engine(5, 10000, 420)
        state = engine.advance(engine.p.state, 12000)
        self.assertEqual((state.hearts, state.next_heart_at), (5, 420))
        state = engine.advance(replace(state, hearts=4), 12000)
        self.assertEqual((state.hearts, state.next_heart_at), (5, 12420))
        state = engine.advance(replace(state, hearts=4), 12000)
        self.assertEqual(state.hearts, 4)

    def test_continuous_recovery_stops_at_cap(self):
        engine = self.engine(1, 0, 100)
        state = engine.advance(engine.p.state, 2000)
        self.assertEqual(state.hearts, 5)
        self.assertEqual(state.next_heart_at, 1780)

    def test_successful_breed_from_full_resets_refill_before_spending(self):
        data = wait_example()
        data['state'].update(hearts=5, now=1000, next_heart_at=420)
        engine = Planner(Problem(data))
        for _, _, state in engine.transitions(engine.p.state, Action('breed', ('low-a', 'low-b'))):
            self.assertEqual((state.hearts, state.next_heart_at), (4, 1421))


class ValidationTests(unittest.TestCase):
    def test_invalid_probabilities_and_money_are_rejected(self):
        for edit in ['probability', 'nan', 'sale_probability', 'currency', 'duplicate_id', 'unknown_config', 'infinite_nonstarter']:
            with self.subTest(edit=edit):
                data = wait_example()
                if edit == 'probability': data['rules'][0]['outcomes'][0]['probability'] = .99
                elif edit == 'nan': data['quotes'][0]['net_proceeds'] = float('nan')
                elif edit == 'sale_probability': data['quotes'][0]['sale_probability'] = 2
                elif edit == 'currency': data['quotes'][0]['currency'] = 'USD'
                elif edit == 'duplicate_id': data['state']['fish'][1]['id'] = 'low-a'
                elif edit == 'unknown_config': data['config']['guess_grade'] = True
                elif edit == 'infinite_nonstarter': data['state']['fish'][0]['unlimited_breeding'] = True
                with self.assertRaises(ValidationError): Problem(data)

    def test_seasonal_result_cannot_be_generated_by_regular_actions(self):
        data = wait_example()
        data['rules'][0]['outcomes'][0]['fish']['seasonal'] = True
        with self.assertRaises(ValidationError): Problem(data)

    def test_outcome_needs_explicit_readiness(self):
        data = wait_example()
        del data['rules'][0]['outcomes'][0]['fish']['ready_after_seconds']
        with self.assertRaises(ValidationError): Problem(data)

    def test_forecasts_match_inspected_rows_and_are_not_grade_probabilities(self):
        self.assertEqual(breed_level_probabilities(5, 5)[5], .55)
        self.assertEqual(breed_level_probabilities(3, 5), breed_level_probabilities(4, 4))
        self.assertEqual(merge_level_probabilities([4] * 9 + [3]), {1: 0, 2: 0, 3: 0, 4: .1, 5: .9})
        self.assertEqual(merge_level_probabilities([5] * 10)[5], 1)


if __name__ == '__main__':
    unittest.main()
