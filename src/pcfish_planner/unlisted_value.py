"""Explicit Season 2 unlisted cohort: estimates from observed legacy peers."""
import math
from .market_coverage import best_state

# Season 2 announcement: 5 regular species plus 3 seasonal species (38--40).
# The seasonal collection and recipe policies always own seasonal fish.
NEW_REGULAR = {'FS00036', 'FS00037', 'FS00041', 'FS00042', 'FS00043'}
NEW_COHORT = NEW_REGULAR | {'FS00038', 'FS00039', 'FS00040'}
SOURCE = 'estimated_peer_max'


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def observed_quote(quote, name, now, ttl):
    if (not isinstance(quote, dict) or quote.get('market_hash_name') != name
            or quote.get('currency_id') != 16 or not finite(quote.get('checked_at'))
            or not 0 <= now - quote['checked_at'] < ttl):
        return None
    if quote.get('coverage_version')==3:
        best=best_state(quote.get('best_state_proof'),name)
        if best is None:return None
        return dict(market_hash_name=name,currency_id=16,checked_at=quote['checked_at'],valid_until=quote['checked_at']+ttl,
            book_complete=True,coverage_version=3,listing_total=best['total'],listing_observed=quote.get('listing_observed',0),
            best_remaining_breeds=best['remaining_breeds'],best_maximum_breeds=best['maximum_breeds'],seller_proceeds_krw=best['seller_proceeds_krw'],
            best_state_proof=quote['best_state_proof'])
    if (quote.get('book_complete') is not True or quote.get('coverage_version') != 2
            or quote.get('listing_total', 0) <= 0 or quote.get('listing_observed',0) < quote['listing_total']
            or quote.get('listing_observed') != len(quote.get('samples', []))):
        return None
    samples = [x for x in quote.get('samples', []) if isinstance(x, dict)
               and type(x.get('remaining_breeds')) is int and type(x.get('maximum_breeds')) is int
               and 0 <= x['remaining_breeds'] <= x['maximum_breeds'] and x['maximum_breeds'] > 0
               and finite(x.get('seller_proceeds_krw')) and x['seller_proceeds_krw'] >= 0]
    if not samples or len(samples) != len(quote.get('samples', [])):
        return None
    best = max(x['remaining_breeds'] for x in samples)
    best_samples = [x for x in samples if x['remaining_breeds'] == best]
    return dict(market_hash_name=name, currency_id=16, checked_at=quote['checked_at'],
                book_complete=True, coverage_version=2, listing_total=len(samples), listing_observed=len(samples),
                valid_until=quote['checked_at'] + ttl, best_remaining_breeds=best,
                best_maximum_breeds=max(x['maximum_breeds'] for x in best_samples),
                seller_proceeds_krw=max(x['seller_proceeds_krw'] for x in best_samples))


def peer_quotes(catalog, cache, now, ttl, new_cohort=None):
    peers = []
    excluded = NEW_COHORT | set(new_cohort or [])
    for entry in catalog:
        if entry.get('seasonal') or entry.get('species') in excluded or entry.get('level', 0) <= 0:
            continue
        for grade, name in enumerate(entry.get('market_names', []), 1):
            q = observed_quote(cache.get(name), name, now, ttl)
            if q is not None:
                peers.append(dict(q, species=entry['species'], grade=grade))
            else:
                empty = cache.get(name)
                if (isinstance(empty, dict) and empty.get('market_hash_name') == name and empty.get('currency_id') == 16
                        and empty.get('book_complete') is True and empty.get('coverage_version') == 2
                        and empty.get('listing_total') == empty.get('listing_observed') == 0
                        and empty.get('samples') == [] and finite(empty.get('checked_at')) and 0 <= now-empty['checked_at'] < ttl):
                    peers.append(dict(species=entry['species'], grade=grade, market_hash_name=name, currency_id=16,
                        checked_at=empty['checked_at'], valid_until=empty['checked_at']+ttl, no_listings=True,
                        book_complete=True, coverage_version=2, listing_total=0, listing_observed=0,
                        best_remaining_breeds=-1, best_maximum_breeds=0, seller_proceeds_krw=0))
    return peers


def reference_products(fish, catalog, cache, config, now, ttl):
    """Request comparable products from the catalog, including unowned fish."""
    if config.get('market_merge_price_basis') != 'best_observed_breeds':
        return {}
    regular = NEW_REGULAR | set(config.get('_discovered_regular_species', []))
    excluded = NEW_COHORT | set(config.get('_discovered_cohort', []))
    targets = {(f['level'], f['grade']) for f in fish if f.get('species') in regular
               and not f.get('basic') and not f.get('seasonal') and not f.get('locked')
               and f['id'] not in config.get('reserved_ids', []) and f['species'] not in config.get('reserved_species', [])
               and 1 <= f['grade'] <= 5
               and observed_quote(cache.get(f.get('market_hash_name')), f.get('market_hash_name'), now, ttl) is None}
    products = {}
    for entry in catalog:
        if entry.get('seasonal') or entry.get('species') in excluded or len(entry.get('market_names', [])) != 5:
            continue
        for level, grade in targets:
            name = entry['market_names'][grade - 1]
            if entry.get('level') == level and name:
                products[name] = dict(species=entry['species'], level=level, grade=grade,
                                      purpose='unlisted_estimate_reference')
    return products


def apply_estimate(fish, review, catalog, peers, config, now):
    regular = NEW_REGULAR | set(config.get('_discovered_regular_species', []))
    if (config.get('market_merge_price_basis') != 'best_observed_breeds'
            or fish.get('species') not in regular or fish.get('seasonal') or fish.get('basic')
            or review.get('product_listings', 0) > 0):
        return review
    entries = {e['species']: e for e in catalog}
    target = entries.get(fish['species'])
    grade = fish['grade']
    if (not target or target.get('seasonal') or target.get('level') != fish['level']
            or not 1 <= grade <= 5 or len(target.get('market_names', [])) != 5
            or target['market_names'][grade - 1] != fish.get('market_hash_name')):
        return review
    matching = [p for p in peers if p['grade'] == grade and entries[p['species']]['level'] == fish['level']]
    excluded = NEW_COHORT | set(config.get('_discovered_cohort', []))
    expected = {e['market_names'][grade-1] for e in catalog if not e.get('seasonal') and e['species'] not in excluded
                and e.get('level') == fish['level'] and len(e.get('market_names', [])) == 5}
    missing = sorted(expected - {p['market_hash_name'] for p in matching})
    if missing:
        return {**review, 'value_source':'unavailable', 'estimate_reason':'Waiting for complete selling-state coverage of comparable species',
                'estimate_missing_products':missing}
    matching = [p for p in matching if not p.get('no_listings')]
    if not matching:
        return {**review, 'value_source': 'unavailable', 'estimate_reason': 'No current same-rarity/star legacy peer quote'}
    price = max(p['seller_proceeds_krw'] for p in matching)
    state = 'release' if price <= config.get('market_min_seller_proceeds_krw', 1000) else 'keep'
    return {**review, 'state': state, 'merge_state': state, 'value_source': SOURCE,
            'estimated_seller_proceeds_krw': price, 'estimate_peer_count': len(matching),
            'estimate_peer_names': sorted(p['market_hash_name'] for p in matching),
            'checked_at': min(p['checked_at'] for p in matching),
            'valid_until': min(p['valid_until'] for p in matching),
            'reason': 'Season 2 unlisted species: maximum observed best-state ask of same-rarity/star legacy peers',
            'merge_reason': 'Estimated peer maximum is at most threshold' if state == 'release' else 'Estimated peer maximum exceeds threshold'}
