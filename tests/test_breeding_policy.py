from copy import deepcopy
import unittest

from pcfish_planner.breeding_policy import MaterialScorer, ObservationModel
from pcfish_planner.bridge import decide
from pcfish_planner.engine import ValidationError
from test_bridge import live_fish
from test_stock_policy import CFG, need, policy_state


def observation(event, grades, child_grade, *, level=1, child_level=1, uses=2, kind='breed', **flags):
    def traits(grade, rarity, species, remaining):
        return dict(species=species, level=rarity, grade=grade, remaining_breeds=remaining,
                    ready_after_seconds=420, unlimited_breeding=False)
    return dict(event_id=event, kind=kind, confirmed=True, synthetic=False,
                parents=[traits(g, level, 'parent', 1) for g in grades],
                result=traits(child_grade, child_level, 'wanted', uses), **flags)


def model(*cases):
    return ObservationModel([observation(f'{j}-{i}', grades, grade, **fields)
                             for j, (grades, grade, fields) in enumerate(cases) for i in range(30)])


class BreedingObservationTests(unittest.TestCase):
    def test_only_real_confirmed_unique_ordinary_events_are_learned(self):
        actual = observation('a', [3, 3], 4)
        records = [actual, deepcopy(actual), {**actual, 'event_id': 'fake', 'synthetic': True},
                   {**actual, 'event_id': 'pending', 'confirmed': False},
                   {**actual, 'event_id': 'comparison', 'flow': 'native_comparison'},
                   {**actual, 'event_id': 'craft', 'kind': 'craft'}]
        learned = ObservationModel(records)
        self.assertEqual(learned.events, 1)
        distribution, count, basis = learned.outcomes('breed', (3, 3), 1)
        self.assertEqual((count, basis), (1, 'unobserved_star_heuristic'))
        self.assertEqual(distribution, {(3, 1): 1})

    def test_sparse_and_cross_rarity_samples_do_not_become_certain_outcomes(self):
        record = observation('one', [4, 4], 5, child_level=2)
        record['parents'][1]['level'] = 3
        learned = ObservationModel([record])
        distribution, count, basis = learned.outcomes('breed', (4, 4), 1, (1, 1))
        self.assertEqual((distribution, count, basis), ({(4, 1): 1.0}, 0, 'unobserved_star_heuristic'))
        distribution, count, basis = learned.outcomes('breed', (4, 4), 2, (1, 3))
        self.assertEqual((distribution, count, basis), ({(4, 1): 1.0}, 1, 'unobserved_star_heuristic'))

    def test_parent_level_grade_association_is_preserved(self):
        records = []
        for i in range(5):
            record = observation(str(i), [2, 4], 5, child_level=2)
            record['parents'][1]['level'] = 3
            records.append(record)
        learned = ObservationModel(records)
        self.assertEqual(learned.outcomes('breed', (2, 4), 2, (1, 3))[1], 5)
        self.assertEqual(learned.outcomes('breed', (4, 2), 2, (3, 1))[1], 5)
        self.assertEqual(learned.outcomes('breed', (4, 2), 2, (1, 3))[1], 0)
        self.assertEqual(learned.outcomes('breed', (2, 4), 1, (1, 3))[1], 0)

    def test_conflicting_event_is_rejected(self):
        with self.assertRaises(ValidationError):
            ObservationModel([observation('same', [1, 1], 1), observation('same', [1, 1], 2)])

    def test_result_rarity_conditions_star_estimate(self):
        learned = model(([2, 2], 1, dict(child_level=1)), ([2, 2], 3, dict(child_level=3)))
        low, _, _ = learned.outcomes('breed', (2, 2), 1)
        high, _, _ = learned.outcomes('breed', (2, 2), 3)
        self.assertGreater(low.get((1, 2), 0), high.get((1, 2), 0))
        self.assertGreater(high.get((3, 2), 0), low.get((3, 2), 0))
        self.assertAlmostEqual(sum(low.values()), 1)
        self.assertAlmostEqual(sum(high.values()), 1)

    def test_unseen_pair_is_labeled_as_heuristic(self):
        learned = model(([1, 1], 1, {}))
        _, count, basis = learned.outcomes('breed', (4, 4), 3)
        self.assertEqual((count, basis), (0, 'unobserved_star_heuristic'))

    def test_unseen_catalog_species_is_not_given_zero_probability(self):
        learned = model(([2, 2], 3, dict(child_level=3)))
        share = learned.species_share('breed', 3, 3, 'unseen', ('wanted', 'unseen'))
        self.assertGreater(share, 0)
        self.assertLess(share, learned.species_share('breed', 3, 3, 'wanted', ('wanted', 'unseen')))


class MaterialSelectionTests(unittest.TestCase):
    def test_high_star_candidates_survive_many_lower_star_fish(self):
        fishes = [live_fish(f'low-{i}', level=1, breeds=5) for i in range(100)]
        fishes += [live_fish('star-a', level=1, grade=3, breeds=2),
                   live_fish('star-b', level=1, grade=3, breeds=2)]
        learned = model(([1, 1], 1, {}), ([1, 3], 1, {}), ([3, 3], 3, {}))
        s = policy_state(fishes, [need(1, grade=3)])
        before = deepcopy(s)
        action = decide(s, {**CFG, '_observation_model': learned}, 0)
        self.assertEqual((action['kind'], set(action['fish_ids'])), ('breed', {'star-a', 'star-b'}))
        self.assertEqual(action['material_evaluation']['sample_count'], 0)  # Other forecast rarities remain unobserved.
        self.assertEqual(s, before)

    def test_evidence_changes_choice_with_same_inventory(self):
        fishes = [live_fish('one-a', level=1), live_fish('one-b', level=1),
                  live_fish('two-a', level=1, grade=2), live_fish('two-b', level=1, grade=2)]
        for successful, ids in [([1, 1], {'one-a', 'one-b'}), ([2, 2], {'two-a', 'two-b'})]:
            cases = [(pair, 3 if pair == successful else 1, {}) for pair in [[1, 1], [1, 2], [2, 2]]]
            action = decide(policy_state(fishes, [need(1, grade=3)]),
                            {**CFG, '_observation_model': model(*cases)}, 0)
            self.assertEqual(set(action['fish_ids']), ids)

    def test_last_material_for_next_recipe_gets_priority(self):
        fishes = [live_fish('three-a', level=1, grade=3), live_fish('three-b', level=1, grade=3),
                  live_fish('four-a', level=1, grade=4), live_fish('four-b', level=1, grade=4)]
        needs = [{**need(1, grade=3), 'reserved': 2, 'missing': 1}, need(1, grade=4), need(1, grade=5)]
        learned = model(([3, 3], 3, {}), ([3, 4], 2, {}), ([4, 4], 4, {}))
        action = decide(policy_state(fishes, needs), {**CFG, '_observation_model': learned}, 0)
        self.assertEqual(set(action['fish_ids']), {'three-a', 'three-b'})
        self.assertEqual(action['material_evaluation']['next_recipe_grade'], 3)

    def test_future_parents_can_be_valuable_before_target_star_is_observed(self):
        fishes = [live_fish(f'low-{i}', level=1, breeds=5) for i in range(10)]
        fishes += [live_fish('high-a', level=3, grade=3), live_fish('high-b', level=3, grade=3)]
        learned = model(([1, 1], 1, {}), ([1, 3], 1, {}), ([3, 3], 4, dict(level=3, child_level=3, uses=3)))
        action = decide(policy_state(fishes, [need(3, grade=5)]), {**CFG, '_observation_model': learned}, 0)
        self.assertEqual(set(action['fish_ids']), {'high-a', 'high-b'})
        evaluation = action['material_evaluation']
        self.assertEqual(evaluation['material_score'], 0)
        self.assertGreater(evaluation['parent_score'], 0)

    def test_parent_value_diminishes_when_reproductive_capacity_is_abundant(self):
        parent = live_fish('a', grade=3, breeds=2)
        s = policy_state([parent], [need(3, grade=5)])
        scorer = MaterialScorer(s, {**CFG, '_stock_plan': s['season']['plan']}, [parent])
        first = scorer.capacity_value((3, 3), 2) - scorer.capacity_value((3, 3), 0)
        extra = scorer.capacity_value((3, 3), 22) - scorer.capacity_value((3, 3), 20)
        self.assertGreater(first, extra)

    def test_later_high_star_needs_contribute_even_when_next_recipe_is_lower(self):
        parent = live_fish('a', grade=3, breeds=2)
        s = policy_state([parent], [{**need(3, grade=3), 'missing': 1, 'reserved': 2},
                                   need(3, grade=4), need(3, grade=5)])
        scorer = MaterialScorer(s, {**CFG, '_stock_plan': s['season']['plan']}, [parent])
        self.assertGreater(scorer.parent_value(3, 4), scorer.parent_value(3, 3))
        self.assertGreater(scorer.parent_value(3, 5), scorer.parent_value(3, 4))

    def test_merge_prefers_needed_stars_and_retains_native_allocations(self):
        low = [live_fish(f'low-{i}', level=1, breeds=0) for i in range(10)]
        high = [live_fish(f'high-{i}', level=1, grade=3, breeds=0) for i in range(10)]
        learned = model(([1] * 10, 1, dict(kind='merge', child_level=2)),
                        ([3] * 10, 3, dict(kind='merge', child_level=2)))
        s = policy_state(low + high, [need(2, grade=3)])
        action = decide(s, {**CFG, '_observation_model': learned}, 0)
        self.assertEqual((action['kind'], set(action['fish_ids'])), ('merge', {f['id'] for f in high}))
        s['season']['plan']['reserved_ids'] = ['high-0']
        s['season']['plan']['exhausted_ids'].remove('high-0')
        self.assertEqual(decide(s, {**CFG, '_observation_model': learned}, 0)['kind'], 'hold')

    def test_wrong_star_merge_is_held_without_a_future_breeding_route(self):
        fishes = [live_fish(str(i), level=2, breeds=0) for i in range(10)]
        learned = model(([1] * 10, 1, dict(kind='merge', child_level=3)))
        action = decide(policy_state(fishes, [need(3, grade=3)]), {**CFG, '_observation_model': learned}, 0)
        self.assertEqual(action['kind'], 'hold')

    def test_disabled_breeding_does_not_justify_wrong_star_merges(self):
        fishes = [live_fish(str(i), level=2, breeds=0) for i in range(10)] + [live_fish('parent', level=2)]
        learned = model(([1] * 10, 1, dict(kind='merge', child_level=3)))
        action = decide(policy_state(fishes, [need(3, grade=3)]),
                        {**CFG, 'auto_breed': False, '_observation_model': learned}, 0)
        self.assertEqual(action['kind'], 'hold')

    def test_market_locked_and_seasonal_parents_stay_protected(self):
        fishes = [live_fish('a', level=1, grade=3), live_fish('b', level=1, grade=3)]
        learned = model(([3, 3], 3, {}))
        for blocked in ['locked', 'seasonal', 'market']:
            s = policy_state(deepcopy(fishes), [need(1, grade=3)])
            cfg = {**CFG, '_observation_model': learned}
            if blocked == 'market':
                cfg['protection_mode'] = 'market'
                for f in s['fish']:
                    f.update(market_state='pending', market_valid_until=100)
            else:
                s['fish'][0][blocked] = True
            self.assertEqual(decide(s, cfg, 0)['kind'], 'hold')

    def test_unknown_pairs_keep_exploration_available(self):
        s = policy_state([live_fish('a', level=1, grade=3), live_fish('b', level=1, grade=3)], [need(1, grade=3)])
        action = decide(s, CFG, 0)
        self.assertEqual(action['kind'], 'breed')
        self.assertEqual(action['material_evaluation']['evidence'], 'unobserved_star_heuristic')

    def test_joint_estimate_has_only_possible_rarities(self):
        fishes = [live_fish('a', level=1, grade=3), live_fish('b', level=1, grade=3)]
        learned = model(([3, 3], 3, dict(child_level=5)))
        action = decide(policy_state(fishes, [need(1, grade=3)]), {**CFG, '_observation_model': learned}, 0)
        weights = action['material_evaluation']['outcome_weights']
        self.assertLessEqual({r['level'] for r in weights}, {1, 2, 3})
        self.assertAlmostEqual(sum(r['weight'] for r in weights), 1)

    def test_full_hearts_explore_when_unseen_target_stars_would_stall(self):
        fishes = [live_fish('a', level=3, grade=4), live_fish('b', level=3, grade=4)]
        for learned in (ObservationModel(), model(([4, 4], 4, dict(child_level=3, uses=1)))):
            s = policy_state(fishes, [need(3, grade=5)])
            cfg = {**CFG, '_observation_model': learned}
            # Ordinary low-heart decisions retain their capacity cost.
            self.assertEqual(decide(s, cfg, 0)['kind'], 'hold')
            s['hearts'] = 5
            action = decide(s, cfg, 0)
            self.assertEqual((action['kind'], set(action['fish_ids'])), ('breed', {'a', 'b'}))
            evaluation = action['material_evaluation']
            self.assertEqual(evaluation['decision_basis'], 'capped_heart_exploration')
            self.assertLessEqual(evaluation['score'], 0)
            self.assertGreater(evaluation['exploration_priority'], 0)

    def test_full_hearts_can_bootstrap_distant_materials_from_starters(self):
        s = policy_state([live_fish('a', level=0), live_fish('b', level=0)], [need(3, grade=5)])
        s['hearts'] = 5
        action = decide(s, CFG, 0)
        self.assertEqual(action['kind'], 'breed')
        self.assertEqual(action['material_evaluation']['decision_basis'], 'capped_heart_exploration')

    def test_exploration_retains_locks_readiness_rejections_and_disable(self):
        s = policy_state([live_fish('a', level=3, grade=4), live_fish('b', level=3, grade=4)], [need(3, grade=5)])
        s['hearts'] = 5
        for blocked in ('locked', 'immature', 'rejected', 'disabled', 'full'):
            state, cfg = deepcopy(s), {**CFG}
            if blocked == 'locked':
                state['fish'][0]['locked'] = True
            elif blocked == 'immature':
                state['fish'][0].update(ready_at=1000, ready=False)
            elif blocked == 'rejected':
                cfg['_deferred_breed_pairs'] = [['a', 'b']]
            elif blocked == 'disabled':
                cfg['auto_breed'] = False
            else:
                state['capacity'] = len(state['fish'])
            self.assertEqual(decide(state, cfg, 0)['kind'], 'hold', blocked)

    def test_exploration_does_not_override_useful_wait(self):
        fishes = [live_fish('a', level=1, grade=1), live_fish('b', level=1, grade=1),
                  live_fish('c', level=1, grade=3, ready=100), live_fish('d', level=1, grade=3, ready=100)]
        learned = model(([1, 1], 1, dict(uses=0)), ([1, 3], 1, dict(uses=0)), ([3, 3], 3, {}))
        s = policy_state(fishes, [need(1, grade=3)])
        s['hearts'] = 5
        s['capacity'] = len(fishes) + 1  # Only one child fits; waiting has a concrete benefit.
        self.assertEqual(decide(s, {**CFG, '_observation_model': learned}, 0)['kind'], 'wait')


if __name__ == '__main__':
    unittest.main()
