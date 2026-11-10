import unittest

from pcfish_planner.merge_policy import merge_batch_allowed
from pcfish_planner.probabilities import merge_level_probabilities


class MergePolicyTests(unittest.TestCase):
    def test_all_adjacent_mixes_match_no_downgrade_client_forecast(self):
        for lower in range(1, 5):
            for high_count in range(1, 10):
                levels = [lower] * (10-high_count) + [lower+1] * high_count
                for grade in range(1, 6):
                    with self.subTest(lower=lower, high_count=high_count, grade=grade):
                        self.assertTrue(merge_batch_allowed(levels, [grade]*10))
                        forecast = merge_level_probabilities(levels)
                        self.assertTrue(all(level >= max(levels) for level,p in forecast.items() if p > 0))

    def test_same_rarity_and_level_five_cap_remain_allowed(self):
        for level in range(1, 6):
            self.assertTrue(merge_batch_allowed([level]*10, [1]*10))
        self.assertEqual(merge_level_probabilities([4]+[5]*9)[5], 1)

    def test_gap_two_is_refused_even_when_forecast_has_no_downgrade(self):
        for levels in ([1]*9+[3], [1]+[3]*9, [3]+[5]*9, [1,2]+[3]*8):
            self.assertFalse(merge_batch_allowed(levels, [1]*10))

    def test_stars_count_and_invalid_materials_cannot_bypass_policy(self):
        for levels,grades in (([1]*9,[1]*9), ([1]*11,[1]*11), ([1]*10,[1]*9+[2]),
                              ([0]+[1]*9,[1]*10), ([6]*10,[1]*10), ([1]*10,[0]*10)):
            self.assertFalse(merge_batch_allowed(levels, grades))

    def test_any_modeled_downgrade_is_refused(self):
        self.assertTrue(merge_batch_allowed([1]*9+[2], [1]*10, [2,3]))
        self.assertFalse(merge_batch_allowed([1]*9+[2], [1]*10, [1,2,3]))
        self.assertFalse(merge_batch_allowed([1]*9+[2], [1]*10, []))
