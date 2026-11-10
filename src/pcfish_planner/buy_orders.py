"""Public highest buy orders, with independently checkable KRW fee evidence.

Steam's current KRW market rounds base prices up and each fee to the nearest
10 won (minimum 10 won). Validate that schedule against the returned listing
fees before using it. An absent/malformed book is unknown, never a zero bid.
"""
import math

BASIS = 'highest_buy_order'
SOURCE = 'estimated_peer_highest_buy_order'
FEE_MODEL = 'steam_krw_round_10_v1'


def integer(n):
    return type(n) is int and 0 <= n <= 1_000_000_000_000


def fees(base):
    # Integer minor units: 100 units/won; 1000 units/market increment.
    return tuple(max(1000, ((base // divisor + 500) // 1000) * 1000) for divisor in (20, 10))


def net_at_bid(gross):
    low, high = 1, gross // 1000
    best = 0
    while low <= high:
        mid = (low + high) // 2
        base = mid * 1000
        if base + sum(fees(base)) <= gross:
            best = base
            low = mid + 1
        else:
            high = mid - 1
    return best


def validate_proof(proof, name):
    try:
        if (not isinstance(proof, dict) or proof['appid'] != 4842670 or proof['currency_id'] != 16
                or proof['market_hash_name'] != name or proof['fee_model'] != FEE_MODEL):
            return None
        highest, count, rows = proof['highest_buy_order_minor'], proof['buy_order_count'], proof['buy_orders']
        if not integer(highest) or not integer(count) or not isinstance(rows, list):
            return None
        if count == 0:
            return dict(seller_proceeds_krw=0.0, highest_buy_order_krw=0.0, buy_order_count=0) if highest == 0 and rows == [] else None
        if highest < 3000 or highest % 1000 or not rows:
            return None
        previous, observed = highest + 1, 0
        for row in rows:
            price, quantity = row['price_minor'], row['quantity']
            if (not integer(price) or not integer(quantity) or quantity == 0 or price <= 0
                    or price % 1000 or price >= previous):
                return None
            previous = price
            observed += quantity
        if rows[0]['price_minor'] != highest or observed > count:
            return None
        samples = proof['fee_samples']
        if not isinstance(samples, list) or not samples:
            return None
        for row in samples:
            base, steam, publisher = row['seller_minor'], row['steam_fee_minor'], row['publisher_fee_minor']
            if (not all(integer(v) for v in (base, steam, publisher)) or base < 1000 or base % 1000
                    or (steam, publisher) != fees(base)):
                return None
        net = net_at_bid(highest)
        return dict(seller_proceeds_krw=net / 100, highest_buy_order_krw=highest / 100, buy_order_count=count) if net else None
    except (KeyError, TypeError, ValueError):
        return None


def parse_proof(queries, name):
    """Only public product/orderbook/listing queries; no wallet or user data."""
    try:
        descriptions = [q['state']['data'] for q in queries if q['queryKey'] == ['market', 'description', 4842670, name]]
        books = [q['state']['data'] for q in queries if q['queryKey'] == ['market', 'orderbook', 4842670, name]]
        if len(descriptions) != 1 or len(books) != 1:
            return None
        description, book = descriptions[0], books[0]
        if description['appid'] != 4842670 or description['market_hash_name'] != name or book['eCurrency'] != 16:
            return None
        compact = book['rgCompactBuyOrders']
        if not isinstance(compact, list) or len(compact) % 2:
            return None
        highest = book['amtMaxBuyOrder']
        # Steam explicitly uses null when its verified order count is zero.
        # A missing field, positive count, or nonempty ladder is still unknown.
        if highest is None and book['cBuyOrders'] == 0 and compact == []:
            highest = 0
        proof = dict(appid=4842670, currency_id=16, market_hash_name=name, fee_model=FEE_MODEL,
                     highest_buy_order_minor=highest, buy_order_count=book['cBuyOrders'],
                     buy_orders=[dict(price_minor=compact[i], quantity=compact[i+1]) for i in range(0, len(compact), 2)],
                     fee_samples=[])
        for q in queries:
            key = q['queryKey']
            if (len(key) != 2 or key[0] != 'market_item_search' or key[1].get('appid') != 4842670
                    or key[1].get('strItemName') != name):
                continue
            for page in q['state']['data']['pages']:
                for row in page['listings']:
                    d = row['description']
                    if (d['appid'] != 4842670 or d['market_hash_name'] != name or row['eCurrency'] != 16
                            or row['publisherFeeApp'] != 4842670 or abs(row['publisherFeePct'] - .1) > 1e-6
                            or row['unFee'] != row['unSteamFee'] + row['unPublisherFee']):
                        return None
                    proof['fee_samples'].append(dict(seller_minor=row['unPrice'], steam_fee_minor=row['unSteamFee'],
                                                     publisher_fee_minor=row['unPublisherFee']))
        return proof if validate_proof(proof, name) is not None else None
    except (KeyError, TypeError, ValueError, AttributeError):
        return None


def observed_quote(quote, name, now, ttl):
    if (not isinstance(quote, dict) or quote.get('market_hash_name') != name or quote.get('currency_id') != 16
            or type(quote.get('checked_at')) not in (int, float) or not math.isfinite(quote['checked_at'])
            or not 0 <= now - quote['checked_at'] < ttl):
        return None
    value = validate_proof(quote.get('buy_order_proof'), name)
    if value is None:
        return None
    return dict(value, market_hash_name=name, currency_id=16, checked_at=quote['checked_at'],
                valid_until=quote['checked_at']+ttl, buy_order_proof=quote['buy_order_proof'], price_basis=BASIS)


def review(fish, quote, config, now, result):
    result.update(value_source='unavailable', price_basis=BASIS, buy_order_proof=None,
                  reason='Awaiting verified highest buy order', merge_reason='Awaiting verified highest buy order')
    value = observed_quote(quote, fish['market_hash_name'], now, config.get('market_quote_ttl_seconds', 86400))
    if value is None:
        return result
    price = value['seller_proceeds_krw']
    state = 'release' if price <= config.get('market_min_seller_proceeds_krw', 1000) else 'keep'
    reason = ('Verified market has no buy orders' if value['buy_order_count'] == 0
              else 'Highest buy order after verified Steam fees')
    result.update(value, state=state, merge_state=state, value_source=BASIS, reason=reason, merge_reason=reason)
    return result
