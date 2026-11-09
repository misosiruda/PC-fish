"""Validate full selling-state partitions independently of a price page."""
import math


def best_state(proof, name):
    try:
        total = proof['total_before']
        if (proof['appid'] != 4842670 or proof['currency_id'] != 16 or proof['market_hash_name'] != name
                or type(total) is not int or total <= 0 or proof['total_after'] != total):
            return None
        count = 0
        best = None
        for remaining, state in enumerate(proof['states']):
            n, prices, maxima = state['listing_total'], state['prefix_seller_prices'], state['prefix_maximum_breeds']
            ids=state['prefix_listing_ids']
            if (state['remaining_breeds'] != remaining or state['query'] != f'"Breeds: {remaining} /"'
                    or state['sort_field'] != 0 or state['sort_direction'] != 1
                    or type(n) is not int or n < 0 or len(prices) != len(maxima) or len(prices) > n
                    or (n > 0 and not prices) or (n == 0 and prices)
                    or any(type(p) not in (int,float) or not math.isfinite(p) or p < 0 for p in prices)
                    or len(ids)!=len(prices) or len(set(ids))!=len(ids)
                    or any(type(m) is not int or m < max(1,remaining) for m in maxima)
                    or state['prefix_remaining_breeds'] != [remaining]*len(prices)):
                return None
            count += n
            if n:
                best = dict(remaining_breeds=remaining, maximum_breeds=max(maxima), seller_proceeds_krw=max(prices), listings=n,observed=len(ids))
        return dict(best, total=total) if best and count == total and best['observed']==best['listings'] else None
    except (KeyError,TypeError,ValueError):
        return None
