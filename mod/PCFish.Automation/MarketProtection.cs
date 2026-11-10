namespace PCFish.Automation;

internal sealed class MarketReport
{
    public int schema_version { get; set; }
    public string session { get; set; } = "";
    public string config_digest { get; set; } = "";
    public MarketReview[] fish { get; set; } = [];
    public MarketPeerQuote[] peer_quotes { get; set; } = [];
    public SeasonDiscoveryEvidence season_evidence { get; set; } = new();
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
    public int product_listings { get; set; }
    public int full_breed_listings { get; set; }
    public double? product_max_seller_proceeds_krw { get; set; }
    public int best_observed_remaining_breeds { get; set; } = -1;
    public int best_observed_listings { get; set; }
    public double? best_observed_seller_proceeds_krw { get; set; }
    public string value_source { get; set; } = "observed";
    public bool book_complete { get; set; }
    public int coverage_version { get; set; }
    public int listing_total { get; set; }
    public int listing_observed { get; set; }
    public SellingStateProof best_state_proof { get; set; } = new();
    public BuyOrderProof buy_order_proof { get; set; } = new();
    public string price_basis { get; set; } = "";
}

internal static class MarketProtection
{
    // Re-evaluate numeric evidence inside the game; never trust a worker's
    // release label alone. Identity includes remaining AND maximum breeds.
    internal static void Apply(FishState f, MarketReport report, Settings cfg, string session, string digest, double now)
        => Apply(f, report, cfg, session, digest, now, []);

    internal static void Apply(FishState f, MarketReport report, Settings cfg, string session, string digest, double now, MarketCatalogEntry[] catalog)
        => Apply(f, report, cfg, session, digest, now, catalog, 0);

    internal static void Apply(FishState f, MarketReport report, Settings cfg, string session, string digest, double now, MarketCatalogEntry[] catalog, int currentSeason)
    {
        f.market_state = "pending";
        f.market_valid_until = 0;
        f.market_seller_proceeds_krw = null;
        f.market_merge_state = "pending";
        f.market_merge_valid_until = 0;
        f.market_best_seller_proceeds_krw = null;
        f.market_value_source = "unavailable";
        f.market_estimated_seller_proceeds_krw = null;
        f.market_estimate_peer_count = 0;
        f.market_book_complete = false;
        f.market_highest_buy_order_krw = null;
        f.market_buy_order_count = 0;
        if (cfg.protection_mode != "market" || f.basic) return;
        if (report == null || report.schema_version != 1 || report.session != session || report.config_digest != digest || report.fish == null) return;
        var matches = report.fish.Where(r => r != null && r.id == f.id).ToArray();
        if (matches.Length != 1) return;
        var r = matches[0];
        if (string.IsNullOrWhiteSpace(f.market_hash_name) || r.species != f.species || r.variant != f.variant || r.grade != f.grade
            || r.remaining_breeds != f.remaining_breeds || r.maximum_breeds != f.maximum_breeds || r.market_hash_name != f.market_hash_name
            || r.currency_id != 16) return;
        if (r.value_source is UnlistedMarketValue.Source or BuyOrderProof.EstimateSource && r.product_listings == 0)
        {
            UnlistedMarketValue.Apply(f, report.peer_quotes, catalog, cfg, now, report.season_evidence, currentSeason);
            return;
        }
        if (!double.IsFinite(r.checked_at) || !double.IsFinite(r.valid_until) || r.checked_at > now || r.checked_at < 0
            || r.valid_until <= now || r.valid_until <= r.checked_at || r.valid_until > r.checked_at + cfg.market_quote_ttl_seconds
            || now - r.checked_at >= cfg.market_quote_ttl_seconds) return;
        f.market_value_source = "observed";
        if (cfg.market_merge_price_basis == BuyOrderProof.Basis)
        {
            if (r.price_basis != BuyOrderProof.Basis || !BuyOrderProof.TryValue(r.buy_order_proof, f.market_hash_name, out var bid)) return;
            f.market_value_source = BuyOrderProof.Basis;
            f.market_state = f.market_merge_state = bid <= cfg.market_min_seller_proceeds_krw ? "release" : "keep";
            f.market_valid_until = f.market_merge_valid_until = r.valid_until;
            f.market_seller_proceeds_krw = f.market_best_seller_proceeds_krw = bid;
            f.market_highest_buy_order_krw = r.buy_order_proof.highest_buy_order_minor / 100.0;
            f.market_buy_order_count = r.buy_order_proof.buy_order_count;
            return;
        }
        if (cfg.market_merge_price_basis == "best_observed_breeds")
        {
            bool covered = r.book_complete && r.coverage_version == 2 && r.listing_total > 0
                && r.listing_observed >= r.listing_total && r.listing_observed == r.product_listings;
            int remaining = r.best_observed_remaining_breeds, listings = r.best_observed_listings, productListings = r.product_listings;
            double? value = r.best_observed_seller_proceeds_krw;
            if (r.book_complete && r.coverage_version == 3 && SellingStateProof.TryBest(r.best_state_proof, f.market_hash_name, out var best))
            { covered = true; remaining = best.remaining; listings = best.listings; productListings = best.total; value = best.price; }
            f.market_book_complete = covered;
            if (covered && productListings > 0 && remaining >= 0 && listings > 0 && listings <= productListings
                && value is double observed && double.IsFinite(observed) && observed >= 0)
            {
                f.market_merge_state = observed <= cfg.market_min_seller_proceeds_krw ? "release" : "keep";
                f.market_merge_valid_until = r.valid_until;
                f.market_best_seller_proceeds_krw = observed;
            }
        }
        else if (r.product_listings > 0 && r.full_breed_listings > 0 && r.full_breed_listings <= r.product_listings
            && r.product_max_seller_proceeds_krw is double best && double.IsFinite(best) && best >= 0)
        {
            f.market_merge_state = best <= cfg.market_min_seller_proceeds_krw ? "release" : "keep";
            f.market_merge_valid_until = r.valid_until;
            f.market_best_seller_proceeds_krw = best;
        }
        if (r.matching_listings <= 0 || r.trades_30d < 0 || r.seller_proceeds_krw is not double price || !double.IsFinite(price) || price < 0) return;
        f.market_valid_until = r.valid_until;
        f.market_seller_proceeds_krw = price;
        f.market_state = price >= cfg.market_min_seller_proceeds_krw ? "keep"
            : (r.complete || r.matching_listings >= 3) && r.trades_30d > 0 ? "release" : "pending";
    }

    internal static bool CanLock(FishState f, Settings c, Snapshot s) => c.enabled && c.protection_mode == "market"
        && c.mode is "protected" or "economic" && !s.ui_busy && !s.request_in_flight && !s.uncertain
        && !f.basic && !f.locked && f.market_value_source is not (UnlistedMarketValue.Source or BuyOrderProof.EstimateSource) && f.market_state == "keep" && f.market_valid_until > s.captured_at
        && (!c.UsesProductMergeValue || c.market_merge_price_basis != "best_observed_breeds" || f.market_book_complete)
        && !(c.UsesProductMergeValue && f.market_merge_state == "release" && f.market_merge_valid_until > s.captured_at);

    internal static bool EnsureLocked(Func<bool> isLocked, Func<bool> toggle)
    {
        if (isLocked()) return false;
        if (!toggle() || !isLocked()) throw new InvalidDataException("Market preservation lock did not persist");
        return true;
    }
}
