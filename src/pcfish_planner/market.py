"""Anonymous Steam listing reads for preservation, never trading or profit forecasts.

All network work runs off the bridge loop. Missing/partial/wrong-currency data
cannot release a fish for consumption. Prices are seller asks, not filled sales.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from html.parser import HTMLParser
import json
import math
from pathlib import Path
import re
import time
from urllib.error import HTTPError
from urllib.parse import quote

APP_ID = 4842670
CURRENCY = 16  # KRW; Steam's integer monetary fields use 100 units per won.


class MarketUnavailable(ValueError):
    def __init__(self, message, *, retry_after_seconds=900, product_specific=False):
        super().__init__(message)
        self.retry_after_seconds = retry_after_seconds
        self.product_specific = product_specific


class SSRParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.active = False
        self.parts = []

    def handle_starttag(self, tag, attrs):
        if tag == 'script' and dict(attrs).get('id') == 'valve-ssr-data':
            self.active = True

    def handle_endtag(self, tag):
        if tag == 'script':
            self.active = False

    def handle_data(self, data):
        if self.active:
            self.parts.append(data)


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def market_settings(config):
    mode = config.get('protection_mode', 'grade')
    threshold = config.get('market_min_seller_proceeds_krw', 1000)
    ttl = config.get('market_quote_ttl_seconds', 86400)
    interval = config.get('market_request_interval_seconds', 30)
    if (mode not in ('grade', 'market') or not finite(threshold) or threshold <= 0
            or not finite(ttl) or not 60 <= ttl <= 86400
            or not finite(interval) or not 30 <= interval <= 3600):
        raise MarketUnavailable('Invalid market protection settings')
    return mode, threshold, ttl, interval


def listing_url(name):
    return f'https://steamcommunity.com/market/listings/{APP_ID}/{quote(name, safe="")}?cc=KR&l=english&currency=16'


def parse_page(html, name, now):
    parser = SSRParser()
    parser.feed(html)
    try:
        ssr = json.loads(''.join(parser.parts))
        query_data = ssr['renderContext']['queryData']
    except (KeyError, TypeError, ValueError) as exc:
        raise MarketUnavailable('Steam listing data unavailable or changed', product_specific=True) from exc
    return parse_query_data(query_data, name, now)


def parse_query_data(query_data, name, now):
    """Validate the same public market data before or after Chrome hydration."""
    try:
        queries = json.loads(query_data)['queries']
        description = next(q['state']['data'] for q in queries if q['queryKey'] == ['market', 'description', APP_ID, name])
        if description['appid'] != APP_ID or description['market_hash_name'] != name:
            raise MarketUnavailable('Market product mismatch')
        search = next(q['state']['data'] for q in queries if q['queryKey'][0] == 'market_item_search'
                      and q['queryKey'][1]['appid'] == APP_ID and q['queryKey'][1]['strItemName'] == name)
        pages = search['pages']
        if len(pages) != 1 or pages[0]['start'] != 0:
            raise MarketUnavailable('Unexpected market listing pagination')
        page = pages[0]
        listings = page['listings']
        total = page['total_count']
        if type(total) is not int or total < len(listings) or type(page['more']) is not bool:
            raise MarketUnavailable('Invalid market listing coverage')
        complete = not page['more'] and total == len(listings)
        samples = []
        for item in listings:
            d = item['description']
            if d['appid'] != APP_ID or d['market_hash_name'] != name or item['eCurrency'] != CURRENCY:
                raise MarketUnavailable('Market product/currency mismatch')
            price, fee = item['unPrice'], item['unFee']
            if type(price) is not int or price < 0 or type(fee) is not int or fee < 0:
                raise MarketUnavailable('Invalid listing money')
            breeds = [re.fullmatch(r'Breeds:\s*(\d+)\s*/\s*(\d+)', v['value'].strip())
                      for v in d.get('descriptions', [])]
            breeds = [m for m in breeds if m]
            if len(breeds) != 1 or int(breeds[0][1]) > int(breeds[0][2]):
                complete = False
                continue
            samples.append({'remaining_breeds': int(breeds[0][1]), 'maximum_breeds': int(breeds[0][2]),
                            'seller_proceeds_krw': price / 100, 'buyer_total_krw': (price + fee) / 100})
        history = next((q['state'].get('data') for q in queries
                        if q['queryKey'] == ['market', 'pricehistory', APP_ID, name]), None)
        trades = 0
        if history and history.get('ecurrency') == CURRENCY:
            for row in history.get('prices', []):
                if (finite(row.get('time')) and now - 30 * 86400 <= row['time'] <= now
                        and type(row.get('purchases')) is int and row['purchases'] >= 0):
                    trades += row['purchases']
        return {'market_hash_name': name, 'checked_at': now, 'currency_id': CURRENCY,
                'complete': complete, 'trades_30d': trades, 'samples': samples, 'source_url': listing_url(name)}
    except (KeyError, TypeError, ValueError, StopIteration, IndexError, AttributeError) as exc:
        raise MarketUnavailable('Steam listing data unavailable or changed', product_specific=True) from exc


def fetch_quote(name):
    # Lazy import keeps the independent planner usable without browser packages.
    from .browser_market import fetch_quote as fetch_chrome_quote
    return fetch_chrome_quote(name)


def review_fish(fish, quote_data, config, now):
    _, threshold, ttl, _ = market_settings(config)
    review = {k: fish.get(k) for k in ('id', 'species', 'variant', 'grade', 'remaining_breeds', 'maximum_breeds', 'market_hash_name')}
    review.update(state='pending', reason='Awaiting matching current Steam listings', checked_at=0,
                  valid_until=0, currency_id=CURRENCY, seller_proceeds_krw=None, matching_listings=0,
                  complete=False, trades_30d=0)
    q = quote_data
    if (not q or q.get('market_hash_name') != fish.get('market_hash_name') or q.get('currency_id') != CURRENCY
            or not finite(q.get('checked_at')) or not 0 <= now - q['checked_at'] < ttl):
        return review
    matches = [x for x in q.get('samples', []) if x.get('remaining_breeds') == fish['remaining_breeds']
               and x.get('maximum_breeds') == fish.get('maximum_breeds')
               and finite(x.get('seller_proceeds_krw')) and x['seller_proceeds_krw'] >= 0]
    if not matches:
        review['reason'] = 'No listing with matching remaining/maximum breeds'
        return review
    # Several cheap listings can establish a usable price even when Steam only
    # exposes its first page. A single cheap outlier on a partial page cannot.
    prices = sorted(x['seller_proceeds_krw'] for x in matches)
    proceeds = prices[min(2, len(prices) - 1)]
    review.update(checked_at=q['checked_at'], valid_until=q['checked_at'] + ttl,
                  seller_proceeds_krw=proceeds, matching_listings=len(matches),
                  complete=q.get('complete') is True, trades_30d=q.get('trades_30d', 0), source_url=q.get('source_url'))
    if proceeds >= threshold:
        review.update(state='keep', reason='Matching seller ask meets preservation threshold')
    elif (review['complete'] or len(matches) >= 3) and type(review['trades_30d']) is int and review['trades_30d'] > 0:
        review.update(state='release', reason='Below threshold with matching listing coverage and recent market activity')
    else:
        review['reason'] = 'Partial listings or no recent trades; preserve pending review'
    return review


class MarketMonitor:
    """One background request at a time, persistent cache and global backoff."""
    def __init__(self, runtime: Path, fetch=fetch_quote):
        self.runtime = runtime
        self.fetch = fetch
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix='steam-market')
        self.future = None
        self.pending_name = None
        self.cache = {}
        self.attempts = {}
        self.failures = {}
        self.next_request_at = 0
        self.error = ''
        try:
            cached = json.loads((runtime / 'market-cache.json').read_text(encoding='utf-8'))
            if cached.get('schema_version') == 1 and isinstance(cached.get('quotes'), dict):
                self.cache = cached['quotes']
                failures = cached.get('failures', {})
                if isinstance(failures, dict):
                    self.failures = {name: failure for name, failure in failures.items()
                                     if isinstance(failure, dict) and finite(failure.get('retry_at'))}
                attempts = cached.get('attempts', {})
                if isinstance(attempts, dict):
                    self.attempts = {name: at for name, at in attempts.items() if finite(at)}
                retry = cached.get('next_request_at', 0)
                if finite(retry):
                    self.next_request_at = retry
        except (OSError, ValueError, TypeError, AttributeError):
            pass

    def update(self, snapshot, config, now):
        # Import locally to keep the bridge's file sharing/replacement policy.
        from .bridge import atomic
        _, _, ttl, interval = market_settings(config)
        if self.future and self.future.done():
            try:
                self.cache[self.pending_name] = self.future.result()
                self.failures.pop(self.pending_name, None)
                self.error = ''
                self.next_request_at = now + interval
            except Exception as exc:
                delay = 3600 if isinstance(exc, HTTPError) and exc.code in (403, 429) else getattr(exc, 'retry_after_seconds', 900)
                self.error = str(exc) if isinstance(exc, MarketUnavailable) else f'Steam lookup unavailable ({type(exc).__name__})'
                if isinstance(exc, MarketUnavailable) and exc.product_specific:
                    self.failures[self.pending_name] = {'retry_at': now + delay, 'error': self.error}
                    self.next_request_at = now + interval
                else:
                    self.next_request_at = now + delay
            self.future = None
            self.pending_name = None
            atomic(self.runtime / 'market-cache.json', {'schema_version': 1, 'quotes': self.cache,
                                                       'attempts': self.attempts, 'failures': self.failures,
                                                       'next_request_at': self.next_request_at})
        fish = snapshot['fish']
        wanted = sorted({f.get('market_hash_name') for f in fish if not f['basic'] and not f['locked'] and not f['seasonal']
                         and f['id'] not in config.get('reserved_ids', []) and f['species'] not in config.get('reserved_species', [])
                         and f.get('market_hash_name')})
        wanted.sort(key=lambda name: self.attempts.get(name, 0))
        if self.future is None and now >= self.next_request_at:
            for name in wanted:
                if now < self.failures.get(name, {}).get('retry_at', 0):
                    continue
                cached = self.cache.get(name)
                if not isinstance(cached, dict) or not finite(cached.get('checked_at')) or not 0 <= now - cached['checked_at'] < ttl:
                    self.pending_name = name
                    self.attempts[name] = now
                    self.future = self.executor.submit(self.fetch, name)
                    self.next_request_at = now + interval
                    # Persist request throttle before another process/session can start.
                    atomic(self.runtime / 'market-cache.json', {'schema_version': 1, 'quotes': self.cache,
                                                               'attempts': self.attempts, 'failures': self.failures,
                                                               'next_request_at': self.next_request_at})
                    break
        reviews = [review_fish(f, self.cache.get(f.get('market_hash_name')), config, now) for f in fish if not f['basic']]
        report = {'schema_version': 1, 'session': snapshot['session'], 'config_digest': snapshot['config_digest'],
                  'lookup_backend': 'selenium_chrome',
                  'captured_at': now, 'price_basis': 'matching seller ask after fees; not an executed sale',
                  'next_request_at': self.next_request_at, 'lookup_in_progress': self.future is not None,
                  'error': self.error, 'product_failures': self.failures, 'fish': reviews}
        atomic(self.runtime / 'market-review.json', report)
        return report

    def close(self):
        self.executor.shutdown(wait=False, cancel_futures=True)
