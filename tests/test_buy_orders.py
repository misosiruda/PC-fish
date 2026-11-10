from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from pcfish_planner import buy_orders
from pcfish_planner.bridge import decide
from pcfish_planner.market import MarketMonitor, MarketUnavailable, parse_page, review_fish
from pcfish_planner.unlisted_value import apply_estimate, peer_quotes
from test_market import NAME, NOW, MARKET, fish, page, html, snapshot

CFG = {**MARKET, 'market_merge_price_basis': buy_orders.BASIS}


def queries(bid=115000, count=3):
    data = page(1000000, more=True, trades=0)  # Expensive asks must not affect a cheap bid.
    row = data[1]['state']['data']['pages'][0]['listings'][0]
    steam, publisher = buy_orders.fees(row['unPrice'])
    row.update(unFee=steam+publisher, unSteamFee=steam, unPublisherFee=publisher,
               publisherFeeApp=4842670, publisherFeePct=.10000000149011612)
    data.append(dict(queryKey=['market', 'orderbook', 4842670, NAME], state=dict(data=dict(
        eCurrency=16, amtMaxBuyOrder=bid, cBuyOrders=count,
        rgCompactBuyOrders=[bid, count] if count else []))))
    return data


def quote(bid=115000, count=3):
    return parse_page(html(queries(bid, count)), NAME, NOW)


class BuyOrderTests(unittest.TestCase):
    def test_failed_fresh_bid_fetch_retains_listings_and_backoff_across_restart(self):
        catalog = [dict(species=species, level=1, seasonal=False,
                        market_names=[name+' '+('★'*g) for g in range(1, 6)])
                   for species, name in [('FS00041', 'New'), ('FS00001', 'Old')]]
        f = fish(species='FS00041', grade=1, level=1, market_hash_name='New ★')
        cheap = quote(0, 0)
        cheap['market_hash_name'] = cheap['buy_order_proof']['market_hash_name'] = 'Old ★'
        own = quote()
        own['market_hash_name'] = 'New ★'
        own['buy_order_proof'] = None
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            monitor = MarketMonitor(root)
            monitor.cache['Old ★'] = cheap
            s = snapshot([f]); s['market_catalog'] = catalog
            with patch('pcfish_planner.browser_market.fetch_quote', return_value=own), \
                    patch('pcfish_planner.market.time.time', return_value=NOW), \
                    patch.object(monitor.seasons, 'update', return_value={}):
                try:
                    monitor.update(s, CFG, NOW)
                    with self.assertRaises(MarketUnavailable):
                        monitor.future.result(timeout=2)
                    report = monitor.update(s, CFG, NOW+1)
                    self.assertEqual(monitor.cache['New ★']['listing_total'], 2)
                    self.assertGreater(monitor.failures['New ★']['retry_at'], NOW+1)
                    row = report['fish'][0]
                    self.assertEqual((row['state'], row['merge_state']), ('pending', 'pending'))
                    self.assertGreater(row['product_listings'], 0)
                finally:
                    monitor.close()
            restarted = MarketMonitor(root)
            try:
                with patch.object(restarted.seasons, 'update', return_value={}):
                    report = restarted.update(s, CFG, NOW+2)
                self.assertIsNone(restarted.future)
                self.assertEqual(report['fish'][0]['merge_state'], 'pending')
                self.assertGreater(report['fish'][0]['product_listings'], 0)
            finally:
                restarted.close()

    def test_highest_bid_net_replaces_asks_and_breed_matching(self):
        for bid, net, state in ((3000, 10, 'release'), (4000, 20, 'release'), (115000, 1000, 'release'),
                                (116000, 1010, 'keep'), (169000, 1470, 'keep')):
            r = review_fish(fish(remaining_breeds=5, maximum_breeds=5), quote(bid), CFG, NOW)
            self.assertEqual((r['seller_proceeds_krw'], r['state'], r['merge_state']), (net, state, state))
            self.assertEqual(r['highest_buy_order_krw'], bid/100)
            self.assertEqual(r['value_source'], buy_orders.BASIS)

    def test_zero_orders_are_zero_value_but_unknown_is_pending(self):
        r = review_fish(fish(), quote(0, 0), CFG, NOW)
        self.assertEqual((r['state'], r['seller_proceeds_krw']), ('release', 0))
        data = queries(0, 0); data[-1]['state']['data']['amtMaxBuyOrder'] = None
        self.assertEqual(review_fish(fish(), parse_page(html(data), NAME, NOW), CFG, NOW)['state'], 'release')
        data[-1]['state']['data']['cBuyOrders'] = 1
        self.assertIsNone(buy_orders.parse_proof(data, NAME))
        data[-1]['state']['data']['cBuyOrders'] = 0
        del data[-1]['state']['data']['amtMaxBuyOrder']
        self.assertIsNone(buy_orders.parse_proof(data, NAME))
        for q in (None, parse_page(html(page()), NAME, NOW), {**quote(), 'checked_at': NOW-86400},
                  {**quote(), 'checked_at': NOW+1}, {**quote(), 'currency_id': 1}):
            self.assertEqual(review_fish(fish(), q, CFG, NOW)['state'], 'pending')

    def test_malformed_evidence_never_releases(self):
        p = quote()['buy_order_proof']
        for field, value in [('appid', 1), ('currency_id', 1), ('market_hash_name', 'Other'),
                             ('highest_buy_order_minor', True), ('highest_buy_order_minor', 1000),
                             ('highest_buy_order_minor', 116000), ('buy_order_count', 2),
                             ('fee_model', 'legacy'), ('fee_samples', []), ('buy_orders', [])]:
            bad = {**deepcopy(p), field:value}
            self.assertIsNone(buy_orders.validate_proof(bad, NAME), field)
        bad = deepcopy(p); bad['fee_samples'][0]['steam_fee_minor'] += 1000
        self.assertIsNone(buy_orders.validate_proof(bad, NAME))
        bad = deepcopy(p); bad['buy_orders'] *= 2
        self.assertIsNone(buy_orders.validate_proof(bad, NAME))
        for q in (queries() + [queries()[-1]], queries()[:-1]):
            self.assertIsNone(buy_orders.parse_proof(q, NAME))

    def test_valid_highest_price_uses_first_bid_not_lowest_tail(self):
        data = queries(116000, 103)
        data[-1]['state']['data']['rgCompactBuyOrders'] = [116000, 3, 4000, 100]
        r = review_fish(fish(), parse_page(html(data), NAME, NOW), CFG, NOW)
        self.assertEqual((r['seller_proceeds_krw'], r['state']), (1010, 'keep'))

    def test_native_fee_rounding_boundary(self):
        for gross in range(3000, 200000, 1000):
            net = buy_orders.net_at_bid(gross)
            self.assertLessEqual(net+sum(buy_orders.fees(net)), gross)
            self.assertGreater(net+1000+sum(buy_orders.fees(net+1000)), gross)

    def test_legacy_cache_refreshed_and_bid_cache_shared_across_breeds(self):
        calls=[]
        def fetch(name): calls.append(name); return quote()
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            (root/'market-cache.json').write_text(json.dumps({'schema_version':1,'quotes':{NAME:parse_page(html(page()),NAME,NOW)}}))
            monitor=MarketMonitor(root, fetch)
            try:
                self.assertIn(NAME,monitor.cache)
                s=snapshot([fish()]); monitor.update(s,CFG,NOW)
                monitor.future.result(timeout=2)
                monitor.update(s,CFG,NOW+1)
                s['fish'].append(fish('new',remaining_breeds=4,maximum_breeds=5))
                report=monitor.update(s,CFG,NOW+121)
                self.assertEqual(calls,[NAME])
                self.assertEqual([r['state'] for r in report['fish']],['release','release'])
            finally: monitor.close()

    def test_bid_release_reaches_planner_but_preserves_locks_and_season(self):
        fishes=[fish(str(i),remaining_breeds=0,market_state='release',market_merge_state='release',
                     market_valid_until=NOW+100,market_merge_valid_until=NOW+100) for i in range(10)]
        s=snapshot(fishes);s['captured_at']=NOW
        self.assertEqual(decide(s,CFG,NOW)['kind'],'merge')
        for field in ('locked','seasonal'):
            changed=deepcopy(s);changed['fish'][0][field]=True
            self.assertNotEqual(decide(changed,CFG,NOW)['kind'],'merge')
        s['season']={'id':2,'plan':{'state':'waiting','reason':'reserved','reserved_ids':['0'],'next_craft':None}}
        self.assertNotEqual(decide(s,{**CFG,'auto_season_craft':True},NOW)['kind'],'merge')

    def test_unlisted_peer_estimates_use_bids_and_require_all_comparables(self):
        def entry(species,name):return dict(species=species,level=1,seasonal=False,market_names=[name+' '+('★'*g) for g in range(1,6)])
        catalog=[entry('FS00041','New'),entry('FS00001','Old A'),entry('FS00002','Old B')]
        f=fish(species='FS00041',grade=1,level=1,market_hash_name='New ★')
        cache={}
        for name,bid in [('Old A ★',4000),('Old B ★',115000)]:
            q=quote(bid);q['market_hash_name']=name;q['buy_order_proof']['market_hash_name']=name;cache[name]=q
        peers=peer_quotes(catalog,cache,NOW,86400,price_basis=buy_orders.BASIS)
        r=apply_estimate(f,review_fish(f,None,CFG,NOW),catalog,peers,CFG,NOW)
        self.assertEqual((r['value_source'],r['estimated_seller_proceeds_krw'],r['merge_state']), (buy_orders.SOURCE,1000,'release'))
        r=apply_estimate(f,review_fish(f,None,CFG,NOW),catalog,peers[:1],CFG,NOW)
        self.assertEqual(r['state'],'pending')
        # Actual zero bids take precedence over peer prices.
        q=quote(0,0);q['market_hash_name']=f['market_hash_name'];q['buy_order_proof']['market_hash_name']=f['market_hash_name']
        r=apply_estimate(f,review_fish(f,q,CFG,NOW),catalog,peers,CFG,NOW)
        self.assertEqual((r['value_source'],r['seller_proceeds_krw']),(buy_orders.BASIS,0))

    def test_listed_new_species_with_unverified_bid_cannot_use_cheap_peers(self):
        def entry(species, name):
            return dict(species=species, level=1, seasonal=False,
                        market_names=[name+' '+('★'*g) for g in range(1, 6)])
        catalog = [entry('FS00041', 'New'), entry('FS00001', 'Old')]
        f = fish(species='FS00041', grade=1, level=1, market_hash_name='New ★')
        cheap = quote(0, 0)
        cheap['market_hash_name'] = cheap['buy_order_proof']['market_hash_name'] = 'Old ★'
        peers = peer_quotes(catalog, {'Old ★': cheap}, NOW, 86400, price_basis=buy_orders.BASIS)
        for source in ('legacy', 'bad_fee', 'expired', 'total_only', 'samples_only'):
            with self.subTest(source=source):
                own = quote()
                own['market_hash_name'] = own['buy_order_proof']['market_hash_name'] = 'New ★'
                if source == 'bad_fee':
                    own['buy_order_proof']['fee_samples'] = []
                elif source == 'expired':
                    own['checked_at'] = NOW - 86400
                else:
                    del own['buy_order_proof']
                if source == 'total_only':
                    own.update(listing_total=1, listing_observed=0, samples=[])
                if source == 'samples_only':
                    own.pop('listing_total'); own.pop('listing_observed')
                reviewed = review_fish(f, own, CFG, NOW)
                self.assertGreater(reviewed['product_listings'], 0)
                final = apply_estimate(f, reviewed, catalog, peers, CFG, NOW)
                self.assertEqual((final['state'], final['merge_state']), ('pending', 'pending'))
                self.assertEqual(final['value_source'], 'unavailable')
