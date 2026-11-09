from pathlib import Path
import json
import tempfile
import unittest

from pcfish_planner.market import MarketMonitor, review_fish
from pcfish_planner.unlisted_value import apply_estimate, peer_quotes, reference_products, SOURCE
from test_bridge import CONFIG, live_fish, snapshot

NOW = 1_800_000_000
CFG = {**CONFIG, 'protection_mode': 'market', 'market_merge_price_basis': 'best_observed_breeds',
       'market_min_seller_proceeds_krw': 1000, 'market_quote_ttl_seconds': 86400}


def entry(species, name, level=1, seasonal=False):
    return dict(species=species, level=level, seasonal=seasonal,
                market_names=[name + ' ' + '★' * g for g in range(1, 6)])


CATALOG = [entry('FS00041', 'Frostblade'), entry('FS00001', 'Old A'), entry('FS00002', 'Old B'),
           entry('FS00011', 'Wrong Rarity', 2), entry('FS00038', 'Seasonal', 3, True)]


def quote(name, prices=(20,), remaining=(2,), checked_at=NOW):
    return dict(market_hash_name=name, checked_at=checked_at, currency_id=16,
                book_complete=True, coverage_version=2, listing_total=len(prices), listing_observed=len(prices),
                samples=[dict(seller_proceeds_krw=p, remaining_breeds=b, maximum_breeds=5)
                         for p, b in zip(prices, remaining)])


class UnlistedValueTests(unittest.TestCase):
    def setUp(self):
        self.fish = {**live_fish('new', level=1, grade=1), 'species': 'FS00041',
                     'maximum_breeds': 3, 'remaining_breeds': 2, 'market_hash_name': 'Frostblade ★'}
        self.cache = {'Old A ★': quote('Old A ★', (10, 50, 3000), (3, 3, 0)),
                      'Old B ★': quote('Old B ★', (20,), (1,)),
                      'Wrong Rarity ★': quote('Wrong Rarity ★', (5000,), (3,)),
                      'Old A ★★★': quote('Old A ★★★', (6000,), (2,))}

    def review(self, fish=None, cache=None, catalog=None):
        f, cache, catalog = fish or self.fish, self.cache if cache is None else cache, catalog or CATALOG
        return apply_estimate(f, review_fish(f, cache.get(f['market_hash_name']), CFG, NOW), catalog,
                              peer_quotes(catalog, cache, NOW, 86400), CFG, NOW)

    def test_maximum_best_observed_peer_ask_ignores_other_rarity_grade_and_worse_state(self):
        r = self.review()
        self.assertEqual((r['value_source'], r['estimated_seller_proceeds_krw'], r['estimate_peer_count']), (SOURCE, 50, 2))
        self.assertEqual((r['state'], r['merge_state']), ('release', 'release'))
        self.assertIsNone(r['seller_proceeds_krw'])
        self.assertEqual(r['estimate_peer_names'], ['Old A ★', 'Old B ★'])

    def test_highest_peer_and_exact_threshold(self):
        for price, state in ((1000, 'release'), (1000.01, 'keep')):
            self.cache['Old B ★'] = quote('Old B ★', (price,), (1,))
            self.assertEqual(self.review()['merge_state'], state)

    def test_fresh_real_listing_takes_precedence_in_both_directions(self):
        for price, expected in ((5, 'release'), (1500, 'keep')):
            self.cache['Frostblade ★'] = quote('Frostblade ★', (price,), (2,))
            r = self.review()
            self.assertNotIn('estimated_seller_proceeds_krw', r)
            self.assertEqual(r['merge_state'], expected)

    def test_no_current_peer_does_not_invent_price(self):
        r = self.review(cache={})
        self.assertEqual(r['merge_state'], 'pending')
        self.assertIn('complete selling-state coverage', r['estimate_reason'])

    def test_expired_future_currency_invalid_money_and_breeds_cannot_be_reference(self):
        for change in ({'checked_at': NOW - 86400}, {'checked_at': NOW + 1}, {'currency_id': 1},
                       {'samples': [dict(remaining_breeds=6, maximum_breeds=5, seller_proceeds_krw=1)]},
                       {'samples': [dict(remaining_breeds=1, maximum_breeds=5, seller_proceeds_krw=float('nan'))]}):
            with self.subTest(change=change):
                self.assertEqual(self.review(cache={'Old A ★': {**quote('Old A ★'), **change}})['merge_state'], 'pending')

    def test_unrelated_unknown_and_seasonal_species_are_not_estimated(self):
        for changed in ({'species': 'FS09999'}, {'seasonal': True}, {'basic': True}, {'grade': 2}):
            self.assertNotIn('estimated_seller_proceeds_krw', self.review(fish={**self.fish, **changed}))

    def test_new_and_seasonal_reference_never_feed_estimates(self):
        catalog = CATALOG + [entry('FS00042', 'Other New'), entry('FS00039', 'Other Seasonal', 1, True)]
        cache = {'Other New ★': quote('Other New ★'), 'Other Seasonal ★': quote('Other Seasonal ★')}
        self.assertEqual(self.review(cache=cache, catalog=catalog)['merge_state'], 'pending')

    def test_expiry_is_bounded_by_oldest_actual_reference(self):
        self.cache['Old B ★']['checked_at'] = NOW - 86390
        self.assertEqual(self.review()['valid_until'], NOW + 10)

    def test_monitor_daily_missing_product_check_does_not_starve_other_products(self):
        calls = []
        def fetch(name):
            calls.append(name)
            return quote(name)
        with tempfile.TemporaryDirectory() as d:
            monitor = MarketMonitor(Path(d), fetch)
            monitor.attempts['Frostblade ★'] = NOW - 900
            s = snapshot([self.fish, live_fish('old', market_hash_name='Other Fish ★')])
            s['market_catalog'] = CATALOG
            try:
                monitor.cache = self.cache.copy()
                report = monitor.update(s, CFG, NOW)
                monitor.future.result(timeout=2)
                self.assertEqual(calls, ['Other Fish ★'])
                self.assertEqual(report['fish'][0]['value_source'], SOURCE)
                self.assertTrue(report['peer_quotes'])
            finally:
                monitor.close()

    def test_known_new_product_rechecks_after_24_hours(self):
        calls = []
        with tempfile.TemporaryDirectory() as d:
            monitor = MarketMonitor(Path(d), lambda name: calls.append(name) or quote(name))
            monitor.attempts['Frostblade ★'] = NOW - 86400
            try:
                monitor.update(snapshot([self.fish]), CFG, NOW)
                monitor.future.result(timeout=2)
                self.assertEqual(calls, ['Frostblade ★'])
            finally:
                monitor.close()

    def test_reference_unowned_same_rarity_star_legacy_products_only(self):
        products = reference_products([self.fish], CATALOG, {}, CFG, NOW, 86400)
        self.assertEqual(set(products), {'Old A ★', 'Old B ★'})
        self.assertEqual(products['Old A ★']['purpose'], 'unlisted_estimate_reference')
        self.assertEqual(reference_products([{**self.fish, 'locked': True}], CATALOG, {}, CFG, NOW, 86400), {})
        self.assertEqual(reference_products([self.fish], CATALOG, {'Frostblade ★': quote('Frostblade ★')}, CFG, NOW, 86400), {})

    def test_missing_peer_cache_triggers_unowned_lookup_with_original_interval_and_persistence(self):
        calls = []
        def fetch(name):
            calls.append(name)
            return quote(name)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            monitor = MarketMonitor(root, fetch)
            monitor.attempts['Frostblade ★'] = NOW - 900
            s = snapshot([self.fish]); s['market_catalog'] = CATALOG
            try:
                monitor.update(s, CFG, NOW)
                monitor.future.result(timeout=2)
                report = monitor.update(s, CFG, NOW + 1)
                self.assertEqual(calls, ['Old A ★'])
                self.assertEqual(report['fish'][0]['merge_state'], 'pending')
                self.assertEqual(report['fish'][0]['estimate_missing_products'], ['Old B ★'])
                records = [json.loads(p.read_text(encoding='utf-8')) for p in (root / 'market-requests').glob('*.json')]
                self.assertEqual(records[0]['purpose'], 'unlisted_estimate_reference')
                monitor.update(s, CFG, NOW + 120)
                self.assertEqual(calls, ['Old A ★'])
                monitor.update(s, CFG, NOW + 121)
                monitor.future.result(timeout=2)
                monitor.update(s, CFG, NOW + 122)
                self.assertEqual(calls, ['Old A ★', 'Old B ★'])
                restarted = MarketMonitor(root, fetch)
                try:
                    restarted.update(s, CFG, NOW + 1000)
                    self.assertIsNone(restarted.future)
                    self.assertEqual(calls, ['Old A ★', 'Old B ★'])
                finally:
                    restarted.close()
            finally:
                monitor.close()


if __name__ == '__main__':
    unittest.main()
