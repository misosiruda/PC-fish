"""Count every selling breeding state; enumerate all listings of the best state."""
from hashlib import sha256
from pathlib import Path
import time

from .browser_market import fetch_quote as base_quote
from .market import MarketUnavailable
from .market_book import query_page, parse_listing_page, PAGE_DELAY_SECONDS
from .market_coverage import best_state


def fetch_quote(name, *, progress_root=None, control_config=None, page_delay_seconds=PAGE_DELAY_SECONDS):
    from .bridge import read_json, atomic
    path = Path(progress_root)/('states-'+sha256(name.encode()).hexdigest()+'.json') if progress_root else None
    if path:path.parent.mkdir(parents=True,exist_ok=True)
    progress = None
    if path and path.exists():
        try:
            saved=read_json(path)
            if (saved.get('kind')=='state_partition' and saved.get('format_version')==2
                    and saved.get('state')=='collecting' and 0<=time.time()-saved['started_at']<600):
                progress=saved
        except (OSError,ValueError,TypeError,KeyError):pass
    if progress is None:
        base=base_quote(name)
        total=base.get('listing_total')
        if type(total) is not int or total<0:
            raise MarketUnavailable('Market total unavailable',product_specific=True)
        if base.get('complete') is True and total==len(base['samples']):
            return dict(base,book_complete=True,coverage_version=2,pages_observed=1)
        progress=dict(kind='state_partition',format_version=2,state='collecting',market_hash_name=name,started_at=time.time(),last_page_at=time.time(),
                      base=base,total=total,total_before=total,states=[],covered_count=0,pages=1)
    while progress['covered_count']<progress['total_before']:
        if control_config and not read_json(Path(control_config)).get('enabled'):
            if path:atomic(path,progress)
            raise MarketUnavailable('Market state collection paused',product_specific=True,retry_after_seconds=60,reason_code='collection_paused')
        wait=max(0,progress['last_page_at']+page_delay_seconds-time.time())
        if wait:time.sleep(wait)
        remaining=len(progress['states'])
        if remaining>64:
            raise MarketUnavailable('Selling-state coverage unavailable',product_specific=True,reason_code='incomplete_state_coverage')
        page=query_page(name,0,direction=1,remaining_breeds=remaining)
        total,rows,more=parse_listing_page(page,name,0)
        if any(r['remaining_breeds']!=remaining for r in rows):
            progress['unverified_prefix']=dict(requested_remaining=remaining,total=total,rows=rows)
            if path:atomic(path,progress)
            raise MarketUnavailable('Steam state filter did not match returned breeding states',product_specific=True,retry_after_seconds=120,reason_code='unverified_state_filter')
        progress['states'].append(dict(remaining_breeds=remaining,listing_total=total,query=f'"Breeds: {remaining} /"',sort_field=0,sort_direction=1,
            prefix_seller_prices=[r['seller_proceeds_krw'] for r in rows],prefix_remaining_breeds=[r['remaining_breeds'] for r in rows],
            prefix_maximum_breeds=[r['maximum_breeds'] for r in rows],prefix_listing_ids=[r['listing_id'] for r in rows]))
        progress['covered_count']+=total;progress['pages']+=1;progress['last_page_at']=time.time()
        if path:atomic(path,progress)
        if progress['covered_count']>progress['total_before']:
            progress['state']='changed'
            if path:atomic(path,progress)
            raise MarketUnavailable('Selling-state counts changed; refresh this product',product_specific=True,retry_after_seconds=120,reason_code='state_counts_changed')
    highest=next(s for s in reversed(progress['states']) if s['listing_total']>0)
    indexed={id:(price,maximum) for id,price,maximum in zip(highest['prefix_listing_ids'],highest['prefix_seller_prices'],highest['prefix_maximum_breeds'])}
    cursor=len(indexed);sweeps=0
    while len(indexed)<highest['listing_total']:
        if control_config and not read_json(Path(control_config)).get('enabled'):
            if path:atomic(path,progress)
            raise MarketUnavailable('Market state collection paused',product_specific=True,retry_after_seconds=60,reason_code='collection_paused')
        wait=max(0,progress['last_page_at']+page_delay_seconds-time.time())
        if wait:time.sleep(wait)
        page=query_page(name,cursor,direction=1-sweeps%2,remaining_breeds=highest['remaining_breeds'])
        total,rows,more=parse_listing_page(page,name,cursor)
        if total!=highest['listing_total'] or any(r['remaining_breeds']!=highest['remaining_breeds'] for r in rows):
            progress['state']='changed'
            if path:atomic(path,progress)
            raise MarketUnavailable('Highest-state listings changed; refresh this product',product_specific=True,retry_after_seconds=120,reason_code='state_counts_changed')
        indexed.update((r['listing_id'],(r['seller_proceeds_krw'],r['maximum_breeds'])) for r in rows)
        cursor+=len(rows);progress['pages']+=1;progress['last_page_at']=time.time()
        if not more:cursor=0;sweeps+=1
        highest.update(prefix_listing_ids=list(indexed),prefix_seller_prices=[v[0] for v in indexed.values()],
            prefix_maximum_breeds=[v[1] for v in indexed.values()],prefix_remaining_breeds=[highest['remaining_breeds']]*len(indexed))
        if path:atomic(path,progress)
        if sweeps>20:raise MarketUnavailable('Highest-state listing IDs remain incomplete',product_specific=True,retry_after_seconds=120,reason_code='incomplete_state_coverage')
    wait=max(0,progress['last_page_at']+page_delay_seconds-time.time())
    if wait:time.sleep(wait)
    final=query_page(name,0)
    total,_,_=parse_listing_page(final,name,0)
    proof=dict(appid=4842670,currency_id=16,market_hash_name=name,total_before=progress['total_before'],total_after=total,states=progress['states'])
    if best_state(proof,name) is None:
        progress['state']='changed'
        if path:atomic(path,progress)
        raise MarketUnavailable('Selling-state counts changed; refresh this product',product_specific=True,retry_after_seconds=120,reason_code='state_counts_changed')
    progress.update(state='complete',last_page_at=time.time(),pages=progress['pages']+1)
    if path:atomic(path,progress)
    return dict(progress['base'],book_complete=True,coverage_version=3,coverage_kind='state_partition',best_state_proof=proof,
        scan_started_at=progress['started_at'],scan_completed_at=progress['last_page_at'],pages_observed=progress['pages'],
        lookup_backend='steam_public_state_partition')
