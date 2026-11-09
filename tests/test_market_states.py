from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from pcfish_planner.market_states import fetch_quote
from pcfish_planner.market_coverage import best_state
from pcfish_planner.market import review_fish,MarketUnavailable,MarketMonitor
from pcfish_planner.unlisted_value import observed_quote
from test_market import fish,MARKET,NAME,NOW
from test_market_book import page,listing
from test_bridge import snapshot


def base():
    return dict(market_hash_name=NAME,currency_id=16,checked_at=NOW,complete=False,trades_30d=10,
                samples=[dict(remaining_breeds=0,maximum_breeds=2,seller_proceeds_krw=20)],listing_total=835,listing_observed=1,listing_has_more=True)


def pages():
    return [page(0,[listing(1,0,2,20000)],702,True),page(0,[listing(2,1,2,500)],103,True),
            page(0,[listing(3+i,2,2,4870 if i==0 else 1510) for i in range(20)],30,True),
            page(20,[listing(23+i,2,2,100) for i in range(10)],30,False),page(0,[listing(1,0,2,20)],835,True)]


class MarketStatesTests(unittest.TestCase):
    def quote(self,p=None):
        with patch('pcfish_planner.market_states.base_quote',return_value=base()), \
             patch('pcfish_planner.market_states.query_page',side_effect=p or pages()) as query, \
             patch('pcfish_planner.market_states.time.time',return_value=NOW):
            q=fetch_quote(NAME,page_delay_seconds=0)
        return q,query

    def test_all_state_counts_and_all_highest_state_listings_prove_value(self):
        q,query=self.quote()
        proof=q['best_state_proof']
        self.assertEqual([s['listing_total'] for s in proof['states']],[702,103,30])
        self.assertEqual(sum(s['listing_total'] for s in proof['states']),835)
        self.assertEqual([c.kwargs.get('remaining_breeds') for c in query.call_args_list],[0,1,2,2,None])
        self.assertEqual(len(proof['states'][2]['prefix_listing_ids']),30)
        self.assertEqual((best_state(proof,NAME)['remaining_breeds'],best_state(proof,NAME)['seller_proceeds_krw']),(2,4870))
        review=review_fish(fish(),q,{**MARKET,'market_merge_price_basis':'best_observed_breeds'},NOW)
        self.assertEqual((review['best_observed_remaining_breeds'],review['best_observed_seller_proceeds_krw'],review['merge_state']),(2,4870,'keep'))
        self.assertEqual(observed_quote(q,NAME,NOW,86400)['seller_proceeds_krw'],4870)
        self.assertEqual(q['trades_30d'],10)

    def test_zero_and_missing_intermediate_states_do_not_require_theoretical_full_capacity(self):
        b=base();b['listing_total']=3
        p=[page(0,[listing(1,0,5,20)],1,False),page(0,[],0,False),page(0,[listing(2,2,3,300),listing(3,2,3,200)],2,False),page(0,[listing(1,0,5,20)],3,True)]
        with patch('pcfish_planner.market_states.base_quote',return_value=b),patch('pcfish_planner.market_states.query_page',side_effect=p):
            q=fetch_quote(NAME,page_delay_seconds=0)
        self.assertEqual(best_state(q['best_state_proof'],NAME)['remaining_breeds'],2)

    def test_filter_mismatch_and_changed_total_never_release(self):
        for kind in ('filter','total'):
            p=pages()
            if kind=='filter':p[2]['listings'][0]=listing(3,0,2,4870)
            if kind=='total':p[-1]['total_count']=836
            with self.subTest(kind=kind),self.assertRaises(MarketUnavailable):self.quote(p)

    def test_price_order_variations_are_normal_and_later_page_can_contain_maximum(self):
        p=pages()
        p[1]['listings']=[listing(2,1,2,870),listing(100,1,2,880)]
        p[2]['listings'].reverse()
        p[3]['listings'][-1]=listing(32,2,2,9000)
        q,_=self.quote(p)
        self.assertEqual(best_state(q['best_state_proof'],NAME)['seller_proceeds_krw'],9000)

    def test_incomplete_or_overlapping_partitions_cannot_be_estimation_reference(self):
        q,_=self.quote()
        for kind in ('count','gap','query','direction','price','duplicate','incomplete'):
            changed=deepcopy(q)
            proof=changed['best_state_proof']
            if kind=='count':proof['states'][0]['listing_total']-=1
            if kind=='gap':proof['states'][1]['remaining_breeds']=3
            if kind=='query':proof['states'][2]['query']='other'
            if kind=='direction':proof['states'][2]['sort_direction']=0
            if kind=='price':proof['states'][2]['prefix_seller_prices'][0]=float('nan')
            if kind=='duplicate':proof['states'][2]['prefix_listing_ids'][1]=proof['states'][2]['prefix_listing_ids'][0]
            if kind=='incomplete':
                for key in ('prefix_listing_ids','prefix_seller_prices','prefix_remaining_breeds','prefix_maximum_breeds'):proof['states'][2][key].pop()
            self.assertIsNone(observed_quote(changed,NAME,NOW,86400))

    def test_cached_first_prefix_proof_is_refreshed_and_complete_proof_is_reused(self):
        q,_=self.quote()
        old=deepcopy(q)
        for s in old['best_state_proof']['states']:s.pop('prefix_listing_ids')
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)
            (root/'market-cache.json').write_text(json.dumps(dict(schema_version=1,quotes={NAME:old})),encoding='utf-8')
            calls=[]
            def fetch(name):calls.append(name);return q
            monitor=MarketMonitor(root,fetch)
            try:
                cfg={**MARKET,'market_merge_price_basis':'best_observed_breeds'}
                monitor.update(snapshot([fish()]),cfg,NOW)
                monitor.future.result(timeout=2)
                monitor.update(snapshot([fish()]),cfg,NOW+1)
                monitor.update(snapshot([fish()]),cfg,NOW+121)
                self.assertEqual(calls,[NAME])
                self.assertIsNone(monitor.future)
            finally:monitor.close()
