import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import urlsplit, parse_qs

from pcfish_planner.market import MarketUnavailable, review_fish, MarketMonitor
from pcfish_planner.market_book import fetch_quote, parse_listing_page, query_page
from pcfish_planner.unlisted_value import observed_quote
from test_market import fish, MARKET, NAME, NOW


def listing(id, remaining, maximum, price):
    return dict(listingid=str(id), unPrice=int(price*100), unFee=20*100, eCurrency=16,
                description=dict(appid=4842670, market_hash_name=NAME,
                    descriptions=[dict(value=f'Breeds: {remaining} / {maximum}')]))


def page(start, rows, total, more):
    return dict(start=start, listings=rows, total_count=total, more=more)


class MarketBookTests(unittest.TestCase):
    def test_old_page_order_delay_migrates_without_changing_server_backoff(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)
            cached=dict(schema_version=1,quotes={},next_request_at=NOW+3600,rate_limit_failures=2,failures={
                'shifted':dict(retry_at=NOW+900,reason_code='listing_book_changed',error='Listings changed during full-book collection; rescan this product'),
                'order':dict(retry_at=NOW+900,reason_code='unverified_state_filter',error='Steam state filter/order was not applied'),
                'limited':dict(retry_at=NOW+7200,reason_code='unavailable',error='HTTP 429')})
            (root/'market-cache.json').write_text(json.dumps(cached),encoding='utf-8')
            monitor=MarketMonitor(root,lambda name:None)
            try:
                self.assertNotIn('shifted',monitor.failures)
                self.assertNotIn('order',monitor.failures)
                self.assertIn('limited',monitor.failures)
                self.assertEqual(monitor.next_request_at,NOW+3600)
                self.assertEqual(monitor.rate_limit_failures,2)
            finally:monitor.close()
    def test_transport_uses_only_public_read_query_and_exact_cursor(self):
        class Response:
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self,size):return json.dumps({'data':page(20,[listing(2,2,3,900)],21,False)}).encode()
        with patch('pcfish_planner.market_book.urlopen',return_value=Response()) as send:
            value=query_page(NAME,20)
        request=send.call_args.args[0]
        self.assertEqual(request.get_method(),'GET')
        query=parse_qs(urlsplit(request.full_url).query)
        self.assertEqual(query['q'],['QueryListingsForItem'])
        self.assertEqual(json.loads(query['qp'][0])[0]['start'],20)
        self.assertEqual(value['start'],20)

    def test_transport_server_limit_stops_without_browser_or_endpoint_fallback(self):
        error=HTTPError('public',429,'limited',{'Retry-After':'7200'},None)
        with patch('pcfish_planner.market_book.urlopen',side_effect=error) as send:
            with self.assertRaises(MarketUnavailable) as caught:query_page(NAME,0)
        self.assertTrue(caught.exception.rate_limited)
        self.assertEqual(caught.exception.retry_after_seconds,7200)
        self.assertEqual(send.call_count,1)

    def test_later_pages_determine_actual_best_existing_state_and_its_highest_ask(self):
        pages = {0: page(0, [listing(1,0,5,20), listing(2,0,2,20)], 5, True),
                 2: page(2, [listing(3,1,3,100), listing(4,2,3,800)], 5, True),
                 4: page(4, [listing(5,2,5,900)], 5, False)}
        calls = []
        def query(name, start, **options): calls.append(start); return pages[start]
        with patch('pcfish_planner.market_book.query_page', side_effect=query), patch('pcfish_planner.market_book.time.time', return_value=NOW):
            q = fetch_quote(NAME, page_delay_seconds=0)
        self.assertEqual(calls, [0,2,4])
        self.assertTrue(q['book_complete'])
        self.assertEqual((q['listing_total'], q['listing_observed'], q['pages_observed']), (5,5,3))
        r = review_fish(fish(), q, {**MARKET, 'market_merge_price_basis':'best_observed_breeds'}, NOW)
        self.assertEqual((r['best_observed_remaining_breeds'], r['best_observed_seller_proceeds_krw'], r['merge_state']), (2,900,'release'))
        self.assertEqual(observed_quote(q, NAME, NOW, 86400)['seller_proceeds_krw'], 900)

    def test_first_page_zero_breeds_can_neither_release_nor_be_estimation_reference(self):
        q = dict(market_hash_name=NAME, currency_id=16, checked_at=NOW, complete=False,
                 listing_total=835, listing_observed=20, samples=[dict(remaining_breeds=0,maximum_breeds=2,seller_proceeds_krw=20)]*20)
        r = review_fish(fish(), q, {**MARKET, 'market_merge_price_basis':'best_observed_breeds'}, NOW)
        self.assertEqual(r['merge_state'], 'pending')
        self.assertIsNone(observed_quote(q, NAME, NOW, 86400))

    def test_all_listings_really_exhausted_can_release_after_full_coverage(self):
        with patch('pcfish_planner.market_book.query_page', return_value=page(0,[listing(1,0,3,20)],1,False)):
            q = fetch_quote(NAME, page_delay_seconds=0)
        r = review_fish(fish(), q, {**MARKET,'market_merge_price_basis':'best_observed_breeds'}, q['checked_at'])
        self.assertEqual((r['best_observed_remaining_breeds'],r['merge_state']), (0,'release'))

    def test_partial_progress_survives_network_failure_and_resumes_at_next_cursor(self):
        with tempfile.TemporaryDirectory() as d:
            progress_root = Path(d) / 'new-progress-directory'
            with patch('pcfish_planner.market_book.query_page', side_effect=[page(0,[listing(1,0,2,20)],2,True),MarketUnavailable('offline')]):
                with self.assertRaises(MarketUnavailable): fetch_quote(NAME, progress_root=progress_root, page_delay_seconds=0)
            with patch('pcfish_planner.market_book.query_page', return_value=page(1,[listing(2,2,2,600)],2,False)) as query:
                q = fetch_quote(NAME, progress_root=progress_root, page_delay_seconds=0)
                query.assert_called_once_with(NAME,1,direction=0)
            self.assertEqual(len(q['samples']),2)

    def test_duplicate_shifted_rows_are_deduplicated_and_missing_ids_revisited(self):
        pages=[page(0,[listing(1,0,2,20)],2,True),page(1,[listing(1,0,2,20)],2,False),page(0,[listing(2,2,2,600)],2,True)]
        with patch('pcfish_planner.market_book.query_page',side_effect=pages) as query:
            q=fetch_quote(NAME,page_delay_seconds=0)
        self.assertEqual([c.kwargs['direction'] for c in query.call_args_list],[0,0,1])
        self.assertEqual({r['listing_id'] for r in q['samples']},{'1','2'})
        self.assertTrue(q['book_complete'])

    def test_ordinary_listing_count_change_is_collected_without_global_error(self):
        pages=[page(0,[listing(1,0,2,20)],2,True),page(1,[listing(2,2,2,600)],3,True),page(2,[listing(3,1,2,50)],3,False)]
        with patch('pcfish_planner.market_book.query_page',side_effect=pages):q=fetch_quote(NAME,page_delay_seconds=0)
        self.assertEqual((q['listing_total_min'],q['listing_total_max'],q['listing_observed']),(2,3,3))

    def test_currency_identity_missing_breed_and_coverage_are_checked_on_every_page(self):
        for kind in ('currency','identity','breeds','cursor','gap'):
            row=listing(1,1,3,20); p=page(0,[row],1,False)
            if kind=='currency':row['eCurrency']=1
            if kind=='identity':row['description']['market_hash_name']='Other'
            if kind=='breeds':row['description']['descriptions']=[]
            if kind=='cursor':p['start']=1
            if kind=='gap':p['total_count']=2;p['more']=True;p['listings']=[]
            with patch('pcfish_planner.market_book.query_page', return_value=p):
                with self.assertRaises(MarketUnavailable): fetch_quote(NAME,page_delay_seconds=0)
