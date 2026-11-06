namespace PCFish.Automation;

internal sealed class MarketReport
{
    public int schema_version { get; set; }
    public string session { get; set; } = "";
    public string config_digest { get; set; } = "";
    public MarketReview[] fish { get; set; } = [];
}

internal sealed class MarketReview
{
    public string id { get; set; } = "";
    public string species { get; set; } = "";
    public string variant { get; set; } = "";
    public int grade { get; set; }
    public int remaining_breeds { get; set; }
    public int maximum_breeds { get; set; }
    public string market_hash_name { get; set; } = "";
    public double checked_at { get; set; }
    public double valid_until { get; set; }
    public int currency_id { get; set; }
    public double? seller_proceeds_krw { get; set; }
    public int matching_listings { get; set; }
    public bool complete { get; set; }
    public int trades_30d { get; set; }
}

internal static class MarketProtection
{
    // Re-evaluate numeric evidence inside the game; never trust a worker's
    // release label alone. Identity includes remaining AND maximum breeds.
    internal static void Apply(FishState f, MarketReport report, Settings cfg, string session, string digest, double now)
    {
        f.market_state = "pending";
        f.market_valid_until = 0;
        f.market_seller_proceeds_krw = null;
        if (cfg.protection_mode != "market" || f.basic) return;
        if (report == null || report.schema_version != 1 || report.session != session || report.config_digest != digest || report.fish == null) return;
        var matches = report.fish.Where(r => r != null && r.id == f.id).ToArray();
        if (matches.Length != 1) return;
        var r = matches[0];
        if (string.IsNullOrWhiteSpace(f.market_hash_name) || r.species != f.species || r.variant != f.variant || r.grade != f.grade
            || r.remaining_breeds != f.remaining_breeds || r.maximum_breeds != f.maximum_breeds || r.market_hash_name != f.market_hash_name
            || r.currency_id != 16 || r.matching_listings <= 0 || r.trades_30d < 0
            || r.seller_proceeds_krw is not double price || !double.IsFinite(price) || price < 0
            || !double.IsFinite(r.checked_at) || !double.IsFinite(r.valid_until) || r.checked_at > now || r.checked_at < 0
            || r.valid_until <= now || r.valid_until <= r.checked_at || r.valid_until > r.checked_at + cfg.market_quote_ttl_seconds
            || now - r.checked_at >= cfg.market_quote_ttl_seconds) return;
        f.market_valid_until = r.valid_until;
        f.market_seller_proceeds_krw = price;
        f.market_state = price >= cfg.market_min_seller_proceeds_krw ? "keep"
            : (r.complete || r.matching_listings >= 3) && r.trades_30d > 0 ? "release" : "pending";
    }

    internal static bool CanLock(FishState f, Settings c, Snapshot s) => c.enabled && c.protection_mode == "market"
        && c.mode is "protected" or "economic" && !s.ui_busy && !s.request_in_flight && !s.uncertain
        && !f.basic && !f.locked && f.market_state == "keep" && f.market_valid_until > s.captured_at;

    internal static bool EnsureLocked(Func<bool> isLocked, Func<bool> toggle)
    {
        if (isLocked()) return false;
        if (!toggle() || !isLocked()) throw new InvalidDataException("Market preservation lock did not persist");
        return true;
    }
}
