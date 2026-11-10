from copy import deepcopy
import json
from pathlib import Path
import tempfile
import time
import unittest
from urllib.error import HTTPError

from pcfish_planner.bridge import decide
from pcfish_planner.market import MarketMonitor, MarketUnavailable, market_settings, parse_page, review_fish
from test_bridge import CONFIG, live_fish, snapshot

NOW = 1_800_000_000
NAME = 'Example Fish ★★★'
MARKET = {**CONFIG, 'protection_mode': 'market', 'market_min_seller_proceeds_krw': 1000}


def fish(id='a', **fields):
    result = live_fish(id, grade=3)
    result.update(maximum_breeds=2, market_hash_name=NAME)
    result.update(fields)
    return result


def page(price=100000, *, more=False, trades=3):
    description = {'appid': 4842670, 'market_hash_name': NAME,
                   'descriptions': [{'value': 'Breeds: 1 / 2'}]}
    listing = {'description': description, 'unPrice': price, 'unFee': 15000, 'eCurrency': 16}
    return [
        {'queryKey': ['market', 'description', 4842670, NAME], 'state': {'data': description}},
        {'queryKey': ['market_item_search', {'appid': 4842670, 'strItemName': NAME}],
         'state': {'data': {'pages': [{'start': 0, 'total_count': 2 if more else 1, 'more': more, 'listings': [listing]}]}}},
        {'queryKey': ['market', 'pricehistory', 4842670, NAME],
         'state': {'data': {'ecurrency': 16, 'prices': [{'time': NOW - 3600, 'purchases': trades}]}}}]


def html(queries):
    data = {'renderContext': {'queryData': json.dumps({'queries': queries})}}
    return '<script id="valve-ssr-data" type="application/json">' + json.dumps(data) + '</script>'


class MarketTests(unittest.TestCase):
    def test_best_observed_state_uses_actual_remaining_breeds_without_requiring_full_capacity(self):
        cfg = {**MARKET, 'market_merge_price_basis': 'best_observed_breeds'}
        q = dict(market_hash_name=NAME, checked_at=NOW, currency_id=16, complete=False, trades_30d=0,
                 samples=[dict(remaining_breeds=1, maximum_breeds=2, seller_proceeds_krw=900),
                          dict(remaining_breeds=1, maximum_breeds=5, seller_proceeds_krw=1000),
                          dict(remaining_breeds=0, maximum_breeds=5, seller_proceeds_krw=2000)])
        q.update(book_complete=True, coverage_version=2, listing_total=3, listing_observed=3)
        review = review_fish(fish(remaining_breeds=3, maximum_breeds=5), q, cfg, NOW)
        self.assertEqual((review['best_observed_remaining_breeds'], review['best_observed_listings']), (1, 2))
        self.assertEqual(review['best_observed_seller_proceeds_krw'], 1000)
        self.assertEqual(review['full_breed_listings'], 0)
        self.assertEqual(review['merge_state'], 'release')
        self.assertEqual(review['state'], 'pending')
        q['samples'][1]['seller_proceeds_krw'] = 1000.01
        self.assertEqual(review_fish(fish(), q, cfg, NOW)['merge_state'], 'keep')

    def test_best_observed_can_be_exhausted_but_never_missing_or_invalid(self):
        cfg = {**MARKET, 'market_merge_price_basis': 'best_observed_breeds'}
        q = dict(market_hash_name=NAME, checked_at=NOW, currency_id=16,
                 samples=[dict(remaining_breeds=0, maximum_breeds=2, seller_proceeds_krw=10),
                          dict(remaining_breeds=0, maximum_breeds=5, seller_proceeds_krw=20)])
        q.update(book_complete=True, coverage_version=2, listing_total=2, listing_observed=2)
        review = review_fish(fish(), q, cfg, NOW)
        self.assertEqual((review['best_observed_remaining_breeds'], review['best_observed_seller_proceeds_krw'], review['merge_state']), (0, 20, 'release'))
        for changed in (None, {**q, 'samples': []}, {**q, 'samples': [dict(remaining_breeds=6, maximum_breeds=5, seller_proceeds_krw=10)]},
                        {**q, 'checked_at': NOW - 86400}, {**q, 'currency_id': 1}, {**q, 'market_hash_name': 'Other'}):
            with self.subTest(quote=changed):
                self.assertEqual(review_fish(fish(), changed, cfg, NOW)['merge_state'], 'pending')

    def test_best_observed_merge_keeps_action_specific_locks_and_season_quota(self):
        cfg = {**MARKET, 'market_merge_price_basis': 'best_observed_breeds'}
        materials = [fish(str(i), remaining_breeds=0, market_state='pending', market_merge_state='release',
                          market_merge_valid_until=NOW + 100) for i in range(10)]
        s = snapshot(materials); s['captured_at'] = NOW
        self.assertEqual(decide(s, cfg, NOW)['kind'], 'merge')
        s['season'] = {'id': 2, 'plan': {'state': 'waiting', 'reason': 'Saving ingredients', 'reserved_ids': ['0'], 'next_craft': None}}
        self.assertNotEqual(decide(s, {**cfg, 'auto_season_craft': True}, NOW)['kind'], 'merge')
        for change in ({'locked': True}, {'market_merge_state': 'keep'}, {'market_merge_valid_until': NOW}):
            changed = deepcopy(s); changed['fish'][0].update(change)
            self.assertNotEqual(decide(changed, cfg, NOW)['kind'], 'merge')

    def test_missing_item_description_is_distinct_from_network_or_schema_failure(self):
        queries = page()[1:2]
        queries[0]['state']['data']['pages'][0].update(total_count=0, listings=[], more=False)
        with self.assertRaises(MarketUnavailable) as caught:
            parse_page(html(queries), NAME, NOW)
        self.assertEqual(caught.exception.reason_code, 'item_description_unavailable')
        self.assertTrue(caught.exception.product_specific)
        self.assertFalse(caught.exception.rate_limited)

    def test_observation_coverage_does_not_claim_to_cover_unread_market_listings(self):
        queries = page(1000, more=True)
        queries[1]['state']['data']['pages'][0]['total_count'] = 3929
        q = parse_page(html(queries), NAME, NOW)
        self.assertEqual((q['listing_total'], q['listing_observed'], q['listing_has_more']), (3929, 1, True))
        review = review_fish(fish(), q, {**MARKET, 'market_merge_price_basis': 'best_observed_breeds'}, NOW)
        self.assertEqual(review['listing_total'], 3929)
        self.assertEqual(review['best_observed_remaining_breeds'], 1)
        self.assertEqual(review['merge_state'], 'pending')

    def test_full_breed_value_can_release_surplus_without_exact_breed_matches_or_recent_sales(self):
        q = dict(market_hash_name=NAME, checked_at=NOW, currency_id=16, complete=False, trades_30d=0,
                 samples=[dict(remaining_breeds=5, maximum_breeds=5, seller_proceeds_krw=1000),
                          dict(remaining_breeds=0, maximum_breeds=2, seller_proceeds_krw=20)])
        f = fish(remaining_breeds=3, maximum_breeds=4)
        review = review_fish(f, q, {**MARKET, 'market_merge_price_basis': 'full_breeds'}, NOW)
        self.assertEqual(review['state'], 'pending')  # Breeding price rules stay separate.
        self.assertEqual(review['merge_state'], 'release')
        self.assertEqual(review['product_max_seller_proceeds_krw'], 1000)
        self.assertEqual(review['full_breed_listings'], 1)
        q['samples'][0]['seller_proceeds_krw'] = 1000.01
        self.assertEqual(review_fish(f, q, MARKET, NOW)['merge_state'], 'keep')

    def test_full_breed_merge_does_not_invent_potential_from_used_or_invalid_listings(self):
        q = dict(market_hash_name=NAME, checked_at=NOW, currency_id=16, samples=[
            dict(remaining_breeds=1, maximum_breeds=5, seller_proceeds_krw=10)])
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['merge_state'], 'pending')
        q['samples'].append(dict(remaining_breeds=5, maximum_breeds=5, seller_proceeds_krw=20))
        q['samples'].append(dict(remaining_breeds=0, maximum_breeds=5, seller_proceeds_krw=2000))
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['merge_state'], 'keep')
        for change in ('stale', 'future', 'currency', 'identity'):
            changed = deepcopy(q)
            if change == 'stale': changed['checked_at'] = NOW - 86400
            if change == 'future': changed['checked_at'] = NOW + 1
            if change == 'currency': changed['currency_id'] = 1
            if change == 'identity': changed['market_hash_name'] = 'Other Fish ★'
            self.assertEqual(review_fish(fish(), changed, MARKET, NOW)['merge_state'], 'pending')

    def test_full_breed_merge_policy_is_action_specific_and_preserves_season_quotas(self):
        cfg = {**MARKET, 'market_merge_price_basis': 'full_breeds'}
        materials = [fish(str(i), remaining_breeds=0, market_state='pending', market_merge_state='release',
                          market_merge_valid_until=NOW + 100) for i in range(10)]
        s = snapshot(materials)
        s['captured_at'] = NOW
        plan = decide(s, cfg, NOW)
        self.assertEqual(plan['kind'], 'merge')
        s.update(season={'id': 2, 'plan': {'state': 'waiting', 'reason': 'Saving ingredients', 'reserved_ids': ['0'], 'next_craft': None}})
        self.assertNotEqual(decide(s, {**cfg, 'auto_season_craft': True}, NOW)['kind'], 'merge')
        for change in ({'locked': True}, {'market_merge_state': 'keep'}, {'market_merge_valid_until': NOW}):
            changed = deepcopy(s)
            changed['fish'][0].update(change)
            self.assertNotEqual(decide(changed, cfg, NOW)['kind'], 'merge')

    def test_real_money_fields_and_exact_threshold(self):
        q = parse_page(html(page()), NAME, NOW)
        self.assertEqual(q['samples'][0]['seller_proceeds_krw'], 1000)
        self.assertEqual(q['samples'][0]['buyer_total_krw'], 1150)
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'keep')
        q = parse_page(html(page(99900)), NAME, NOW)
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'release')

    def test_no_net_estimate_from_buyer_total(self):
        q = parse_page(html(page(90000)), NAME, NOW)
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'release')

    def test_wrong_currency_identity_structure_and_login_fail_closed(self):
        for change in ('currency', 'identity', 'price', 'coverage', 'query'):
            queries = page()
            listing = queries[1]['state']['data']['pages'][0]['listings'][0]
            if change == 'currency': listing['eCurrency'] = 1
            if change == 'identity': listing['description']['market_hash_name'] = 'Other Fish ★★★'
            if change == 'price': listing['unPrice'] = True
            if change == 'coverage': queries[1]['state']['data']['pages'][0]['total_count'] = 0
            if change == 'query': queries[1]['queryKey'][1]['strItemName'] = 'Other'
            with self.subTest(change=change), self.assertRaises(MarketUnavailable):
                parse_page(html(queries), NAME, NOW)
        with self.assertRaises(MarketUnavailable):
            parse_page('<html>Log in</html>', NAME, NOW)

    def test_missing_stale_future_or_nonmatching_breeds_stay_pending(self):
        q = parse_page(html(page()), NAME, NOW)
        for changed in (None, {**q, 'checked_at': NOW - 86400}, {**q, 'checked_at': NOW + 1}):
            self.assertEqual(review_fish(fish(), changed, MARKET, NOW)['state'], 'pending')
        for changed in (fish(remaining_breeds=0), fish(maximum_breeds=3)):
            self.assertEqual(review_fish(changed, q, MARKET, NOW)['state'], 'pending')

    def test_partial_low_or_inactive_prices_never_release(self):
        for options in ({'more': True}, {'trades': 0}):
            q = parse_page(html(page(100, **options)), NAME, NOW)
            self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'pending')
        q = parse_page(html(page(100000, more=True, trades=0)), NAME, NOW)
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'keep')

    def test_three_matching_cheap_listings_can_release_partial_page(self):
        queries = page(100, more=True)
        p = queries[1]['state']['data']['pages'][0]
        p['listings'] = [deepcopy(p['listings'][0]) for _ in range(3)]
        p['total_count'] = 10000
        q = parse_page(html(queries), NAME, NOW)
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'release')
        p['listings'][1]['unPrice'] = 100000
        p['listings'][2]['unPrice'] = 150000
        q = parse_page(html(queries), NAME, NOW)
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'keep')

    def test_invalid_missing_breed_description_prevents_release(self):
        queries = page(100)
        queries[1]['state']['data']['pages'][0]['listings'][0]['description']['descriptions'] = []
        q = parse_page(html(queries), NAME, NOW)
        self.assertFalse(q['complete'])
        self.assertEqual(review_fish(fish(), q, MARKET, NOW)['state'], 'pending')

    def test_market_policy_allows_reviewed_cheap_five_star_breed_and_merge(self):
        parents = [fish(id, market_state='release', market_valid_until=NOW + 50) for id in ('a', 'b')]
        for f in parents: f['grade'] = 5
        s = snapshot(parents); s['captured_at'] = NOW; s['next_heart_at'] = NOW + 420
        self.assertEqual(decide(s, MARKET, NOW)['kind'], 'breed')
        s['fish'] = [fish(str(i), remaining_breeds=0, market_state='release', market_valid_until=NOW + 50) for i in range(10)]
        for f in s['fish']: f['grade'] = 5
        self.assertEqual(decide(s, MARKET, NOW)['kind'], 'merge')

    def test_unknown_or_keep_expired_locked_seasonal_and_reserved_stay_out(self):
        base = [fish(id, market_state='release', market_valid_until=NOW + 50) for id in ('a', 'b')]
        for field in ({'market_state': 'pending'}, {'market_state': 'keep'}, {'market_valid_until': NOW},
                      {'market_valid_until': float('nan')}, {'locked': True}, {'seasonal': True}):
            parents = deepcopy(base); parents[0].update(field)
            s = snapshot(parents); s['captured_at'] = NOW; s['next_heart_at'] = NOW + 420
            self.assertEqual(decide(s, MARKET, NOW)['kind'], 'hold')
        s = snapshot(base); s['captured_at'] = NOW; s['next_heart_at'] = NOW + 420
        self.assertEqual(decide(s, {**MARKET, 'reserved_ids': ['a']}, NOW)['kind'], 'hold')

    def test_starter_is_exempt_and_legacy_policy_is_preserved(self):
        s = snapshot([live_fish('a', level=0), live_fish('b', level=0)])
        self.assertEqual(decide(s, MARKET, 0)['kind'], 'breed')
        self.assertEqual(decide(snapshot([fish('a'), fish('b')]), CONFIG, 0)['kind'], 'hold')

    def test_invalid_policy_values_reject(self):
        for field in ({'market_min_seller_proceeds_krw': 0}, {'market_min_seller_proceeds_krw': True},
                      {'market_quote_ttl_seconds': float('nan')}, {'market_request_interval_seconds': 1}):
            with self.assertRaises(MarketUnavailable): market_settings({**MARKET, **field})

    def test_background_lookup_cache_restart_dedup_and_expiry(self):
        calls = []
        def fetch(name):
            calls.append(name)
            return parse_page(html(page()), name, NOW)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            monitor = MarketMonitor(root, fetch)
            s = snapshot([fish('a'), fish('b')])
            try:
                monitor.update(s, MARKET, NOW)
                monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(calls, [NAME])
                self.assertEqual([r['state'] for r in report['fish']], ['keep', 'keep'])
                other = MarketMonitor(root, fetch)
                try:
                    other.update(s, MARKET, NOW + 35)
                    self.assertEqual(calls, [NAME])
                    self.assertIsNone(other.future)
                    report = other.update(s, MARKET, NOW + 86401)
                    self.assertEqual(report['fish'][0]['state'], 'pending')
                finally: other.close()
            finally: monitor.close()

    def test_24_hour_cache_reused_for_new_fish_and_changed_breed_count(self):
        calls = []
        def fetch(name):
            calls.append(name)
            return parse_page(html(page()), name, NOW)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); monitor = MarketMonitor(root, fetch)
            s = snapshot([fish()])
            try:
                monitor.update(s, MARKET, NOW)
                monitor.future.result(timeout=2)
                monitor.update(s, MARKET, NOW + 1)
                restarted = MarketMonitor(root, fetch)
                try:
                    s['fish'].append(fish('newborn'))
                    for age in (3601, 12 * 3600, 86399):
                        report = restarted.update(s, MARKET, NOW + age)
                        self.assertIsNone(restarted.future)
                        self.assertEqual(calls, [NAME])
                        self.assertEqual(report['fish'][1]['state'], 'keep')
                    s['fish'][0]['remaining_breeds'] = 0
                    report = restarted.update(s, MARKET, NOW + 86399)
                    self.assertEqual(report['fish'][0]['state'], 'pending')
                    self.assertIsNone(restarted.future)
                    self.assertEqual(calls, [NAME])
                    report = restarted.update(s, MARKET, NOW + 86400)
                    self.assertEqual(report['fish'][1]['state'], 'pending')
                    self.assertIsNotNone(restarted.future)
                    restarted.future.result(timeout=2)
                    self.assertEqual(calls, [NAME, NAME])
                finally: restarted.close()
            finally: monitor.close()

    def test_rate_limit_backoff_persists_and_never_releases(self):
        def fetch(name): raise HTTPError('https://steamcommunity.com', 429, 'Too Many Requests', {}, None)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); monitor = MarketMonitor(root, fetch)
            s = snapshot([fish()])
            try:
                monitor.update(s, MARKET, NOW)
                with self.assertRaises(HTTPError): monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(report['next_request_at'], NOW + 3601)
                self.assertEqual(report['fish'][0]['state'], 'pending')
                self.assertIsNone(monitor.future)
                other = MarketMonitor(root, fetch)
                try:
                    self.assertGreater(other.next_request_at, time.time())
                finally: other.close()
            finally: monitor.close()

    def test_failed_product_does_not_starve_other_products_after_backoff(self):
        calls = []
        def fetch(name):
            calls.append(name)
            raise MarketUnavailable('Unavailable product')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); monitor = MarketMonitor(root, fetch)
            s = snapshot([fish('a', market_hash_name='A ★★★'), fish('b', market_hash_name='B ★★★')])
            try:
                monitor.update(s, MARKET, NOW)
                with self.assertRaises(MarketUnavailable): monitor.future.result(timeout=2)
                monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(calls, ['A ★★★'])
                other = MarketMonitor(root, fetch)
                try:
                    other.update(s, MARKET, NOW + 901)
                    with self.assertRaises(MarketUnavailable): other.future.result(timeout=2)
                    self.assertEqual(calls, ['A ★★★', 'B ★★★'])
                finally: other.close()
            finally: monitor.close()

    def test_product_failure_does_not_delay_other_products_and_survives_restart(self):
        calls = []
        def fetch(name):
            calls.append(name)
            if name == 'A ★★★':
                raise MarketUnavailable('No matching market data', product_specific=True)
            return {**parse_page(html(page()), NAME, NOW), 'market_hash_name': name}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); monitor = MarketMonitor(root, fetch)
            s = snapshot([fish('a', market_hash_name='A ★★★'), fish('b', market_hash_name='B ★★★')])
            try:
                monitor.update(s, MARKET, NOW)
                with self.assertRaises(MarketUnavailable): monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(report['next_request_at'], NOW + 121)
                self.assertEqual(report['product_failures']['A ★★★']['retry_at'], NOW + 901)
                other = MarketMonitor(root, fetch)
                try:
                    other.update(s, MARKET, NOW + 121)
                    other.future.result(timeout=2)
                    other.update(s, MARKET, NOW + 122)
                    self.assertEqual(calls, ['A ★★★', 'B ★★★'])
                    other.update(s, MARKET, NOW + 242)
                    self.assertIsNone(other.future)
                    self.assertEqual(calls, ['A ★★★', 'B ★★★'])
                finally: other.close()
            finally: monitor.close()

    def test_pending_outcome_suspends_market_but_reuses_quotes_and_resumes(self):
        calls = []
        def fetch(name):
            calls.append(name)
            return parse_page(html(page()), name, NOW)
        with tempfile.TemporaryDirectory() as directory:
            monitor = MarketMonitor(Path(directory), fetch)
            s = snapshot([fish()])
            try:
                for field in ('uncertain', 'request_in_flight'):
                    s[field] = True
                    report = monitor.update(s, MARKET, NOW)
                    self.assertTrue(report['suspended_for_pending_result'])
                    self.assertIsNone(monitor.future)
                    s[field] = False
                self.assertEqual(calls, [])
                monitor.update(s, MARKET, NOW + 1)
                monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 2)
                self.assertEqual(calls, [NAME])
                s['uncertain'] = True
                report = monitor.update(s, MARKET, NOW + 3)
                self.assertEqual(report['fish'][0]['state'], 'keep')
                audit = list((Path(directory) / 'market-requests').glob('*.json'))
                self.assertEqual(len(audit), 1)
                self.assertEqual(json.loads(audit[0].read_text(encoding='utf-8'))['outcome'], 'success')
            finally:
                monitor.close()

    def test_retry_after_and_repeated_rate_limit_survive_restart(self):
        def fetch(name):
            raise HTTPError('https://steamcommunity.com', 429, 'Too Many Requests', {'Retry-After': '7200'}, None)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            monitor = MarketMonitor(root, fetch)
            s = snapshot([fish()])
            try:
                monitor.update(s, MARKET, NOW)
                with self.assertRaises(HTTPError): monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(report['next_request_at'], NOW + 7201)
                other = MarketMonitor(root, fetch)
                try:
                    self.assertEqual(other.rate_limit_failures, 1)
                    other.update(s, MARKET, NOW + 7200)
                    self.assertIsNone(other.future)
                    other.update(s, MARKET, NOW + 7201)
                    with self.assertRaises(HTTPError): other.future.result(timeout=2)
                    report = other.update(s, MARKET, NOW + 7202)
                    self.assertEqual(other.rate_limit_failures, 2)
                    self.assertEqual(report['next_request_at'], NOW + 14402)
                finally: other.close()
            finally: monitor.close()
