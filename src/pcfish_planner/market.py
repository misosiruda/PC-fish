"""Anonymous Steam listings and explicit unlisted-cohort preservation estimates.

All network work runs off the bridge loop. Ordinary missing/wrong-currency data
stays pending; announced unlisted species can use valid same-rarity/star peers.
Prices are seller asks or labelled estimates, not filled sales.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from html.parser import HTMLParser
import json
import math
from pathlib import Path
import re
import time
import uuid
from hashlib import sha256
from email.utils import parsedate_to_datetime
from urllib.error import HTTPError
from urllib.parse import quote
from .unlisted_value import NEW_COHORT, apply_estimate, peer_quotes, reference_products
from .season_discovery import SeasonDiscovery, fetch_official_news
from .market_coverage import best_state

APP_ID = 4842670
CURRENCY = 16  # KRW; Steam's integer monetary fields use 100 units per won.


class MarketUnavailable(ValueError):
    def __init__(self, message, *, retry_after_seconds=900, product_specific=False, rate_limited=False, reason_code='unavailable'):
        super().__init__(message)
        self.retry_after_seconds = retry_after_seconds
        self.product_specific = product_specific
        self.rate_limited = rate_limited
        self.reason_code = reason_code


def retry_delay(headers, now, fallback=3600):
    value = next((v for k, v in (headers or {}).items() if k.lower() == 'retry-after'), None)
    try:
        delay = float(value)
    except (ValueError, TypeError):
        try:
            delay = parsedate_to_datetime(value).timestamp() - now
        except (TypeError, ValueError, OverflowError):
            delay = fallback
    return max(fallback, delay) if finite(delay) else fallback


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
    interval = config.get('market_request_interval_seconds', 120)
    merge_basis = config.get('market_merge_price_basis', 'exact_breeds')
    if (mode not in ('grade', 'market') or not finite(threshold) or threshold <= 0
            or not finite(ttl) or not 60 <= ttl <= 86400
            or not finite(interval) or not 30 <= interval <= 3600 or merge_basis not in ('exact_breeds', 'full_breeds', 'best_observed_breeds')):
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
        description = next((q['state'].get('data') for q in queries if q['queryKey'] == ['market', 'description', APP_ID, name]), None)
        if not isinstance(description, dict):
            raise MarketUnavailable('Steam item description unavailable for this exact product',
                                    product_specific=True, reason_code='item_description_unavailable')
        if description['appid'] != APP_ID or description['market_hash_name'] != name:
            raise MarketUnavailable('Market product mismatch', product_specific=True, reason_code='product_mismatch')
        search = next(q['state']['data'] for q in queries if q['queryKey'][0] == 'market_item_search'
                      and q['queryKey'][1]['appid'] == APP_ID and q['queryKey'][1]['strItemName'] == name)
        pages = search['pages']
        if len(pages) != 1 or pages[0]['start'] != 0:
            raise MarketUnavailable('Unexpected market listing pagination', product_specific=True, reason_code='unsupported_pagination')
        page = pages[0]
        listings = page['listings']
        total = page['total_count']
        if type(total) is not int or total < len(listings) or type(page['more']) is not bool:
            raise MarketUnavailable('Invalid market listing coverage', product_specific=True, reason_code='invalid_coverage')
        complete = not page['more'] and total == len(listings)
        samples = []
        for item in listings:
            d = item['description']
            if d['appid'] != APP_ID or d['market_hash_name'] != name or item['eCurrency'] != CURRENCY:
                raise MarketUnavailable('Market product/currency mismatch', product_specific=True, reason_code='listing_identity_or_currency_mismatch')
            price, fee = item['unPrice'], item['unFee']
            if type(price) is not int or price < 0 or type(fee) is not int or fee < 0:
                raise MarketUnavailable('Invalid listing money', product_specific=True, reason_code='invalid_listing_money')
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
                'complete': complete, 'trades_30d': trades, 'samples': samples, 'source_url': listing_url(name),
                'listing_total': total, 'listing_observed': len(listings), 'listing_has_more': page['more']}
    except MarketUnavailable:
        raise  # Preserve a specific diagnosis instead of catching our ValueError subclass.
    except (KeyError, TypeError, ValueError, StopIteration, IndexError, AttributeError) as exc:
        raise MarketUnavailable('Steam listing data unavailable or changed', product_specific=True, reason_code='invalid_listing_data') from exc


def fetch_quote(name, **options):
    from .market_states import fetch_quote as fetch_book
    return fetch_book(name, **options)


def review_fish(fish, quote_data, config, now):
    _, threshold, ttl, _ = market_settings(config)
    review = {k: fish.get(k) for k in ('id', 'species', 'variant', 'grade', 'remaining_breeds', 'maximum_breeds', 'market_hash_name')}
    review.update(state='pending', reason='Awaiting matching current Steam listings', checked_at=0,
                  valid_until=0, currency_id=CURRENCY, seller_proceeds_krw=None, matching_listings=0,
                  complete=False, trades_30d=0, merge_state='pending',
                  merge_reason='Awaiting actual observed market listings' if config.get('market_merge_price_basis') == 'best_observed_breeds'
                  else 'Awaiting current full-breed listings')
    review.update(book_complete=False, coverage_version=0)
    q = quote_data
    if (not q or q.get('market_hash_name') != fish.get('market_hash_name') or q.get('currency_id') != CURRENCY
            or not finite(q.get('checked_at')) or not 0 <= now - q['checked_at'] < ttl):
        return review
    samples = [x for x in q.get('samples', []) if type(x.get('remaining_breeds')) is int and type(x.get('maximum_breeds')) is int
               and 0 <= x['remaining_breeds'] <= x['maximum_breeds'] and x['maximum_breeds'] > 0
               and finite(x.get('seller_proceeds_krw')) and x['seller_proceeds_krw'] >= 0]
    full = [x for x in samples if x['remaining_breeds'] == x['maximum_breeds']]
    best_remaining = max((x['remaining_breeds'] for x in samples), default=-1)
    best_samples = [x for x in samples if x['remaining_breeds'] == best_remaining]
    partition = best_state(q.get('best_state_proof'),fish.get('market_hash_name')) if q.get('coverage_version')==3 else None
    book_complete = (q.get('book_complete') is True and q.get('coverage_version') == 2
                     and q.get('listing_total', 0) > 0 and q.get('listing_observed',0) >= q['listing_total']
                     and q.get('listing_observed') == len(samples)
                     and len(samples) == len(q.get('samples', [])))
    review.update(checked_at=q['checked_at'], valid_until=q['checked_at'] + ttl,
                  product_listings=len(samples), full_breed_listings=len(full),
                  product_max_seller_proceeds_krw=max((x['seller_proceeds_krw'] for x in samples), default=None),
                  best_observed_remaining_breeds=best_remaining, best_observed_listings=len(best_samples),
                  best_observed_seller_proceeds_krw=max((x['seller_proceeds_krw'] for x in best_samples), default=None),
                  listing_total=q['listing_total'] if type(q.get('listing_total')) is int else 0,
                  listing_observed=q['listing_observed'] if type(q.get('listing_observed')) is int else len(samples),
                  listing_has_more=q.get('listing_has_more', not q.get('complete', False)),
                  book_complete=book_complete, coverage_version=q.get('coverage_version', 0),
                  merge_state='pending', merge_reason='No full-breed listing for this species/star grade')
    review['best_state_proof']=q.get('best_state_proof')
    if partition:
        book_complete=True
        review.update(book_complete=True,product_listings=partition['total'],best_observed_remaining_breeds=partition['remaining_breeds'],
                      best_observed_listings=partition['listings'],best_observed_seller_proceeds_krw=partition['seller_proceeds_krw'])
    if config.get('market_merge_price_basis') == 'best_observed_breeds':
        review['merge_reason'] = 'Awaiting coverage of all currently selling breeding states'
        if (best_samples or partition) and book_complete:
            cheap = review['best_observed_seller_proceeds_krw'] <= threshold
            review.update(merge_state='release' if cheap else 'keep',
                          merge_reason='Highest seller ask among the best actually observed breed state is at most threshold' if cheap
                          else 'Best actually observed breed state exceeds the target sale price')
    elif full:
        # Current star grade, unused breeding capacity. The maximum across ALL
        # observed valid listings also protects against a pricier non-full listing.
        cheap = review['product_max_seller_proceeds_krw'] <= threshold
        review.update(merge_state='release' if cheap else 'keep',
                      merge_reason='Full-breed product observed; highest seller ask is at most threshold' if cheap
                      else 'Observed product can exceed the target sale price')
    matches = [x for x in samples if x.get('remaining_breeds') == fish['remaining_breeds']
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
        self.seasons = SeasonDiscovery(runtime, fetch_official_news if fetch is fetch_quote else None)
        self.future = None
        self.pending_name = None
        self.cache = {}
        self.attempts = {}
        self.failures = {}
        self.next_request_at = 0
        self.error = ''
        self.rate_limit_failures = 0
        self.pending_id = None
        self.pending_started_at = 0
        self.pending_purpose = 'owned_fish'
        self.resumed_book_products = []
        try:
            cached = json.loads((runtime / 'market-cache.json').read_text(encoding='utf-8'))
            if cached.get('schema_version') == 1 and isinstance(cached.get('quotes'), dict):
                self.cache = cached['quotes']
                failures = cached.get('failures', {})
                if isinstance(failures, dict):
                    self.failures = {name: failure for name, failure in failures.items()
                                     if isinstance(failure, dict) and finite(failure.get('retry_at'))}
                    # The old collector treated ordinary equal-price page
                    # shifts as a failure. The resumable ID collector resolves
                    # that cause; preserve server backoffs and request pacing.
                    self.resumed_book_products = [name for name,failure in self.failures.items()
                        if (failure.get('reason_code') == 'listing_book_changed'
                        and failure.get('error') == 'Listings changed during full-book collection; rescan this product')
                        or (failure.get('reason_code') == 'unverified_state_filter'
                        and failure.get('error') == 'Steam state filter/order was not applied')]
                    self.failures = {name:failure for name,failure in self.failures.items() if name not in self.resumed_book_products}
                attempts = cached.get('attempts', {})
                if isinstance(attempts, dict):
                    self.attempts = {name: at for name, at in attempts.items() if finite(at)}
                retry = cached.get('next_request_at', 0)
                if finite(retry):
                    self.next_request_at = retry
                failures_count = cached.get('rate_limit_failures', 0)
                if type(failures_count) is int and 0 <= failures_count <= 4:
                    self.rate_limit_failures = failures_count
        except (OSError, ValueError, TypeError, AttributeError):
            pass

    def update(self, snapshot, config, now):
        # Import locally to keep the bridge's file sharing/replacement policy.
        from .bridge import atomic, read_json
        _, _, ttl, interval = market_settings(config)
        # Public news collection is optional and never blocks game planning.
        try:
            season_evidence = self.seasons.update(snapshot, now)
        except (OSError, ValueError, TypeError, KeyError) as exc:
            season_evidence = {'state': 'unavailable', 'reason': type(exc).__name__}
        config = {**config, '_discovered_regular_species': season_evidence.get('new_regular_species', []),
                  '_discovered_cohort': [e['species'] for e in season_evidence.get('new_species', []) if e.get('confirmed')]}
        if self.future and self.future.done():
            observed_http = 0
            try:
                self.cache[self.pending_name] = self.future.result()
                observed_http = self.cache[self.pending_name].get('http_responses_observed', 0)
                self.failures.pop(self.pending_name, None)
                self.error = ''
                self.rate_limit_failures = 0
                self.next_request_at = now + interval
            except Exception as exc:
                observed_http = getattr(exc, 'http_responses_observed', 0)
                limited = isinstance(exc, HTTPError) and exc.code in (403, 429) or getattr(exc, 'rate_limited', False)
                delay = retry_delay(exc.headers, now) if isinstance(exc, HTTPError) and limited else getattr(exc, 'retry_after_seconds', 900)
                if limited:
                    self.rate_limit_failures = min(4, self.rate_limit_failures + 1)
                    delay = max(delay, min(21600, 3600 * 2 ** (self.rate_limit_failures - 1)))
                self.error = str(exc) if isinstance(exc, MarketUnavailable) else f'Steam lookup unavailable ({type(exc).__name__})'
                if isinstance(exc, MarketUnavailable) and exc.product_specific:
                    self.failures[self.pending_name] = {'retry_at': now + delay, 'error': self.error,
                                                       'reason_code': getattr(exc, 'reason_code', 'unavailable')}
                    self.next_request_at = now + interval
                else:
                    self.next_request_at = now + delay
            if self.pending_id:
                atomic(self.runtime / 'market-requests' / (self.pending_id + '.json'), {
                    'request_id': self.pending_id, 'captured_at': self.pending_started_at, 'completed_at': now,
                    'product': self.pending_name, 'outcome': 'unavailable' if self.error else 'success',
                    'next_request_at': self.next_request_at, 'error': self.error, 'purpose': self.pending_purpose,
                    'http_responses_observed': observed_http})
            self.future = None
            self.pending_name = None
            atomic(self.runtime / 'market-cache.json', {'schema_version': 1, 'quotes': self.cache,
                                                       'attempts': self.attempts, 'failures': self.failures,
                                                       'next_request_at': self.next_request_at, 'rate_limit_failures': self.rate_limit_failures})
        fish = snapshot['fish']
        catalog = snapshot.get('market_catalog', [])
        references = reference_products(fish, catalog, self.cache, config, now, ttl)
        owned = {f.get('market_hash_name') for f in fish if not f['basic'] and not f['locked'] and not f['seasonal']
                         and f['id'] not in config.get('reserved_ids', []) and f['species'] not in config.get('reserved_species', [])
                         and f.get('market_hash_name')}
        wanted = sorted(owned | references.keys())
        resuming = set()
        if self.future is None and now >= self.next_request_at and not snapshot.get('request_in_flight') and not snapshot.get('uncertain'):
            for name in wanted:
                path = self.runtime / 'market-book-progress' / (sha256(name.encode()).hexdigest()+'.json')
                try:
                    p = read_json(path)
                    if p.get('state') == 'collecting' and 0 <= now-p['started_at'] < 14400:
                        resuming.add(name)
                except (OSError, ValueError, TypeError, KeyError):
                    pass
        wanted.sort(key=lambda name: (0 if name in resuming else 1, 0 if name in references else 1,
                    -references.get(name, {}).get('level', 0), -references.get(name, {}).get('grade', 0), self.attempts.get(name, 0)))
        cohort = NEW_COHORT | set(config['_discovered_cohort'])
        new_names = {f.get('market_hash_name') for f in fish if f['species'] in cohort}
        suspended = bool(snapshot.get('request_in_flight') or snapshot.get('uncertain'))
        if not suspended and self.future is None and now >= self.next_request_at:
            for name in wanted:
                # Known new products need a daily availability check, not a
                # repeated 15-minute missing-description lookup. Ordinary
                # failures and the global request/backoff policy stay intact.
                cached = self.cache.get(name)
                if name in new_names and not isinstance(cached, dict) and now - self.attempts.get(name, -ttl) < ttl:
                    continue
                if now < self.failures.get(name, {}).get('retry_at', 0):
                    continue
                needs_book = config.get('market_merge_price_basis') == 'best_observed_breeds' and not (
                    isinstance(cached, dict) and cached.get('book_complete') is True and (
                        cached.get('coverage_version') == 2 or cached.get('coverage_version') == 3
                        and best_state(cached.get('best_state_proof'), name) is not None))
                if needs_book or not isinstance(cached, dict) or not finite(cached.get('checked_at')) or not 0 <= now - cached['checked_at'] < ttl:
                    self.pending_name = name
                    self.pending_id = uuid.uuid4().hex
                    self.pending_started_at = now
                    self.pending_purpose = references.get(name, {}).get('purpose', 'owned_fish')
                    (self.runtime / 'market-requests').mkdir(exist_ok=True)
                    atomic(self.runtime / 'market-requests' / (self.pending_id + '.json'), {
                        'request_id': self.pending_id, 'captured_at': now, 'product': name, 'outcome': 'started',
                        'purpose': self.pending_purpose})
                    self.attempts[name] = now
                    options = {}
                    if self.fetch is fetch_quote:
                        config_path = self.runtime.parent / 'config' / 'pcfish-automation.json'
                        options = dict(progress_root=self.runtime / 'market-book-progress',
                                       control_config=config_path if config_path.exists() else None)
                    self.future = self.executor.submit(self.fetch, name, **options)
                    self.next_request_at = now + interval
                    # Persist request throttle before another process/session can start.
                    atomic(self.runtime / 'market-cache.json', {'schema_version': 1, 'quotes': self.cache,
                                                               'attempts': self.attempts, 'failures': self.failures,
                                                               'next_request_at': self.next_request_at, 'rate_limit_failures': self.rate_limit_failures})
                    break
        peers = peer_quotes(catalog, self.cache, now, ttl, cohort)
        reviews = [apply_estimate(f, review_fish(f, self.cache.get(f.get('market_hash_name')), config, now),
                                  catalog, peers, config, now) for f in fish if not f['basic']]
        report = {'schema_version': 1, 'session': snapshot['session'], 'config_digest': snapshot['config_digest'],
                  'lookup_backend': 'steam_public_state_partition' if self.fetch is fetch_quote else 'custom',
                  'merge_price_basis': config.get('market_merge_price_basis', 'exact_breeds'),
                  'captured_at': now, 'price_basis': 'matching seller ask after fees; not an executed sale',
                  'next_request_at': self.next_request_at, 'lookup_in_progress': self.future is not None,
                  'suspended_for_pending_result': suspended,
                  'error': self.error, 'product_failures': self.failures, 'fish': reviews, 'peer_quotes': peers,
                  'reference_products': references, 'season_evidence': season_evidence,
                  'resumed_book_products': self.resumed_book_products}
        atomic(self.runtime / 'market-review.json', report)
        return report

    def close(self):
        self.executor.shutdown(wait=False, cancel_futures=True)
        self.seasons.close()
