"""Sequential public listing-book reads, with resumable coverage evidence."""
from hashlib import sha256
import json
from pathlib import Path
import re
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from .market import APP_ID, CURRENCY, MarketUnavailable, listing_url, retry_delay

PAGE_DELAY_SECONDS = 5
COVERAGE_VERSION = 2
MAX_LISTINGS = 20000


def query_page(name, start, *, direction=0, remaining_breeds=None):
    params = dict(appid=APP_ID, strItemName=name, filters={}, accessoryFilters={}, propertyFilters={}, start=start,
                  sort=dict(field=0,direction=direction))
    if remaining_breeds is not None:
        params['strQuery']=f'"Breeds: {remaining_breeds} /"'
    url = 'https://steamcommunity.com/market/actions?' + urlencode(dict(
        q='QueryListingsForItem', qp=json.dumps([params], ensure_ascii=False), currency=CURRENCY, cc='KR', l='english'))
    request = Request(url, headers={'x-valve-request-type':'queryAction', 'Accept':'application/json',
        'Accept-Language':'en-US,en;q=0.9', 'Cache-Control':'no-cache', 'User-Agent':'Mozilla/5.0 PCFishPlanner/market-evidence'})
    try:
        with urlopen(request, timeout=30) as response:
            raw = response.read(8_000_001)
        if len(raw) > 8_000_000:
            raise MarketUnavailable('Market listing page exceeds response budget', product_specific=True)
        return json.loads(raw)['data']
    except HTTPError as exc:
        if exc.code in (403, 429):
            raise MarketUnavailable(f'Steam listing query limited (HTTP {exc.code})', rate_limited=True,
                retry_after_seconds=retry_delay(dict(exc.headers), time.time())) from exc
        raise MarketUnavailable(f'Steam listing query unavailable (HTTP {exc.code})', product_specific=exc.code == 404) from exc
    except MarketUnavailable:
        raise
    except (URLError, TimeoutError, KeyError, ValueError) as exc:
        raise MarketUnavailable('Public market listing page unavailable') from exc


def parse_listing_page(page, name, expected_start):
    try:
        if not isinstance(page, dict) or page['start'] != expected_start or type(page['more']) is not bool:
            raise ValueError('cursor')
        total, listings = page['total_count'], page['listings']
        if type(total) is not int or not 0 <= total <= MAX_LISTINGS or not isinstance(listings, list):
            raise ValueError('coverage')
        if expected_start + len(listings) > total and listings or page['more'] and not listings:
            raise ValueError('coverage')
        rows = []
        for item in listings:
            description = item['description']
            if description['appid'] != APP_ID or description['market_hash_name'] != name or item['eCurrency'] != CURRENCY:
                raise ValueError('identity/currency')
            price, fee, listing_id = item['unPrice'], item['unFee'], str(item['listingid'])
            if type(price) is not int or type(fee) is not int or min(price, fee) < 0 or not listing_id.isdigit():
                raise ValueError('money/identity')
            breeds = [re.fullmatch(r'Breeds:\s*(\d+)\s*/\s*(\d+)', d['value'].strip()) for d in description.get('descriptions', [])]
            breeds = [b for b in breeds if b]
            if len(breeds) != 1 or not 0 <= int(breeds[0][1]) <= int(breeds[0][2]) or int(breeds[0][2]) <= 0:
                raise ValueError('breeding state')
            rows.append(dict(listing_id=listing_id, remaining_breeds=int(breeds[0][1]), maximum_breeds=int(breeds[0][2]),
                             seller_proceeds_krw=price/100, buyer_total_krw=(price+fee)/100))
        if len({r['listing_id'] for r in rows}) != len(rows):
            raise ValueError('duplicate')
        return total, rows, page['more']
    except (KeyError, TypeError, ValueError, IndexError) as exc:
        raise MarketUnavailable('Market listing book changed or contains unverified rows', product_specific=True,
                                reason_code='incomplete_listing_book') from exc


def fetch_quote(name, *, progress_root=None, control_config=None, page_delay_seconds=PAGE_DELAY_SECONDS):
    from .bridge import atomic, read_json
    path = Path(progress_root) / (sha256(name.encode()).hexdigest() + '.json') if progress_root else None
    if path:
        path.parent.mkdir(parents=True, exist_ok=True)
    progress = dict(market_hash_name=name, started_at=time.time(), next_start=0, total=None, samples=[], pages=0, passes=0,
                    ordering_version=2, sort_direction=0, state='collecting')
    if path and path.exists():
        try:
            previous = read_json(path)
            if previous.get('market_hash_name') == name and previous.get('state') == 'collecting' and 0 <= time.time()-previous['started_at'] < 14400:
                progress = previous
                if progress.get('ordering_version') != 2:
                    progress.update(ordering_version=2, sort_direction=1, next_start=0)
        except (OSError, ValueError, KeyError, TypeError):
            pass
    ids = {r['listing_id'] for r in progress['samples']}
    requests_sent = 0
    while True:
        if control_config:
            try:
                if not read_json(Path(control_config)).get('enabled'):
                    raise MarketUnavailable('Market book collection paused', product_specific=True, retry_after_seconds=60,
                                            reason_code='collection_paused')
            except OSError:
                raise MarketUnavailable('Market book control state unavailable', product_specific=True)
        if progress['pages']:
            wait = max(0, progress.get('last_page_at',0) + page_delay_seconds - time.time())
            if wait: time.sleep(wait)
        requests_sent += 1
        try:
            page = query_page(name, progress['next_start'], direction=progress['sort_direction'])
        except MarketUnavailable as exc:
            exc.http_responses_observed = requests_sent
            raise
        total, rows, more = parse_listing_page(page, name, progress['next_start'])
        progress['total_min'] = min(progress.get('total_min', total), total)
        progress['total_max'] = max(progress.get('total_max', total), total)
        progress['total'] = total
        # Equal-price ordering and ordinary sales can shift page boundaries.
        # Keep unique IDs and revisit ranges instead of failing the product.
        existing = {r['listing_id']:r for r in progress['samples']}
        existing.update((r['listing_id'],r) for r in rows)
        progress['samples'] = list(existing.values())
        ids.update(r['listing_id'] for r in rows)
        progress['next_start'] += len(rows)
        progress['pages'] += 1
        progress['last_page_at'] = time.time()
        if not more or progress['next_start'] >= total:
            if more and progress['next_start'] < total:
                raise MarketUnavailable('Listing book has an unobserved range', product_specific=True, reason_code='incomplete_listing_book')
            progress['passes'] = progress.get('passes',0) + 1
            progress['sort_direction'] = 1-progress['sort_direction']
            # Alternating direction and offset phase avoids repeating exactly
            # the same unstable equal-price boundary on every sweep.
            progress['next_start'] = (progress['passes']//2) % 20
        if progress.get('passes',0) and len(ids) >= progress['total_max']:
            progress['state'] = 'complete'
        if progress['pages'] > 3000 or time.time()-progress['started_at'] >= 14400:
            raise MarketUnavailable('Listing book coverage is still incomplete; resume this product later', product_specific=True,
                                    reason_code='incomplete_listing_book')
        if path: atomic(path, progress)
        if progress['state'] == 'complete':
            return dict(market_hash_name=name, currency_id=CURRENCY, checked_at=progress['started_at'],
                complete=True, book_complete=True, coverage_version=COVERAGE_VERSION, trades_30d=0,
                samples=progress['samples'], listing_total=total, listing_observed=len(ids), listing_has_more=False,
                source_url=listing_url(name), lookup_backend='steam_public_query_action',
                listing_total_min=progress['total_min'], listing_total_max=progress['total_max'], passes_observed=progress['passes'],
                scan_started_at=progress['started_at'], scan_completed_at=progress['last_page_at'], pages_observed=progress['pages'],
                http_responses_observed=requests_sent)
