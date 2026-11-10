namespace PCFish.Automation;

internal sealed class BuyOrderRow
{
    public long price_minor { get; set; }
    public long quantity { get; set; }
}

internal sealed class MarketFeeSample
{
    public long seller_minor { get; set; }
    public long steam_fee_minor { get; set; }
    public long publisher_fee_minor { get; set; }
}

internal sealed class BuyOrderProof
{
    internal const string Basis = "highest_buy_order";
    internal const string EstimateSource = "estimated_peer_highest_buy_order";
    public int appid { get; set; }
    public int currency_id { get; set; }
    public string market_hash_name { get; set; } = "";
    public string fee_model { get; set; } = "";
    public long highest_buy_order_minor { get; set; } = -1;
    public long buy_order_count { get; set; } = -1;
    public BuyOrderRow[] buy_orders { get; set; } = [];
    public MarketFeeSample[] fee_samples { get; set; } = [];

    static bool Amount(long n) => n is >= 0 and <= 1_000_000_000_000;
    static long Fee(long value, long divisor) => Math.Max(1000, ((value / divisor + 500) / 1000) * 1000);
    internal static long Net(long gross)
    {
        long low = 1, high = gross / 1000, best = 0;
        while (low <= high)
        {
            long mid = (low + high) / 2, value = mid * 1000;
            if (value + Fee(value, 20) + Fee(value, 10) <= gross) { best = value; low = mid + 1; }
            else high = mid - 1;
        }
        return best;
    }

    // Recompute the price from raw order/fee evidence, never trust a release flag.
    internal static bool TryValue(BuyOrderProof p, string name, out double value)
    {
        value = 0;
        if (p == null || p.appid != 4842670 || p.currency_id != 16 || p.market_hash_name != name
            || p.fee_model != "steam_krw_round_10_v1" || !Amount(p.highest_buy_order_minor) || !Amount(p.buy_order_count)
            || p.buy_orders == null) return false;
        if (p.buy_order_count == 0) return p.highest_buy_order_minor == 0 && p.buy_orders.Length == 0;
        if (p.highest_buy_order_minor < 3000 || p.highest_buy_order_minor % 1000 != 0 || p.buy_orders.Length == 0) return false;
        long previous = p.highest_buy_order_minor + 1, count = 0;
        foreach (var row in p.buy_orders)
        {
            if (row == null || !Amount(row.price_minor) || !Amount(row.quantity) || row.quantity == 0
                || row.price_minor <= 0 || row.price_minor % 1000 != 0 || row.price_minor >= previous) return false;
            previous = row.price_minor;
            count += row.quantity;
            if (count > p.buy_order_count) return false;
        }
        if (p.buy_orders[0].price_minor != p.highest_buy_order_minor || p.fee_samples == null || p.fee_samples.Length == 0) return false;
        foreach (var row in p.fee_samples)
            if (row == null || !Amount(row.seller_minor) || !Amount(row.steam_fee_minor) || !Amount(row.publisher_fee_minor)
                || row.seller_minor < 1000 || row.seller_minor % 1000 != 0
                || row.steam_fee_minor != Fee(row.seller_minor, 20) || row.publisher_fee_minor != Fee(row.seller_minor, 10)) return false;
        long net = Net(p.highest_buy_order_minor);
        value = net / 100.0;
        return net > 0;
    }
}
