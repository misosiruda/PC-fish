namespace PCFish.Automation;

internal sealed class MarketCatalogEntry
{
    public string species { get; set; } = "";
    public int level { get; set; }
    public bool seasonal { get; set; }
    public string[] market_names { get; set; } = [];
}

internal sealed class MarketPeerQuote
{
    public string species { get; set; } = "";
    public int grade { get; set; }
    public string market_hash_name { get; set; } = "";
    public int currency_id { get; set; }
    public double checked_at { get; set; }
    public double valid_until { get; set; }
    public int best_remaining_breeds { get; set; } = -1;
    public int best_maximum_breeds { get; set; }
    public double seller_proceeds_krw { get; set; } = double.NaN;
    public bool book_complete { get; set; }
    public int coverage_version { get; set; }
    public int listing_total { get; set; }
    public int listing_observed { get; set; }
    public bool no_listings { get; set; }
    public SellingStateProof best_state_proof { get; set; } = new();
}

internal sealed class SeasonArticleEvidence
{
    public int season { get; set; }
    public string gid { get; set; } = "";
    public string url { get; set; } = "";
    public double published_at { get; set; }
}

internal sealed class DiscoveredSpeciesEvidence
{
    public string species { get; set; } = "";
    public string name { get; set; } = "";
    public bool seasonal { get; set; }
    public string identification { get; set; } = "";
}

internal sealed class SeasonDiscoveryEvidence
{
    public string state { get; set; } = "";
    public int season { get; set; }
    public SeasonArticleEvidence announcement { get; set; } = new();
    public DiscoveredSpeciesEvidence[] new_species { get; set; } = [];
    public string[] new_regular_species { get; set; } = [];
}

internal static class UnlistedMarketValue
{
    internal const string Source = "estimated_peer_max";
    internal static bool NewRegular(string species) => species is "FS00036" or "FS00037" or "FS00041" or "FS00042" or "FS00043";
    static bool NewCohort(string species) => NewRegular(species) || species is "FS00038" or "FS00039" or "FS00040";

    // Catalog comes from the game's current CSV, never from worker claims.
    internal static bool Apply(FishState f, MarketPeerQuote[] peers, MarketCatalogEntry[] catalog, Settings c, double now)
        => Apply(f, peers, catalog, c, now, new(), 0);

    internal static bool Apply(FishState f, MarketPeerQuote[] peers, MarketCatalogEntry[] catalog, Settings c, double now,
        SeasonDiscoveryEvidence discovery, int currentSeason)
    {
        var discovered = VerifiedNewSpecies(discovery, currentSeason, catalog, now);
        if (c.market_merge_price_basis != "best_observed_breeds" || !(NewRegular(f.species) || discovered.Contains(f.species)) || f.basic || f.seasonal
            || catalog == null || peers == null || f.grade is < 1 or > 5) return false;
        var target = catalog.SingleOrDefault(e => e.species == f.species);
        if (target == null || target.seasonal || target.level != f.level || target.market_names?.Length != 5
            || target.market_names[f.grade - 1] != f.market_hash_name) return false;
        var valid = peers.Where(p =>
        {
            if (p == null || p.grade != f.grade || NewCohort(p.species) || discovered.Contains(p.species) || p.currency_id != 16
                || !p.book_complete || p.coverage_version is not (2 or 3) || p.listing_total < 0
                || !double.IsFinite(p.checked_at) || !double.IsFinite(p.valid_until) || p.checked_at < 0 || p.checked_at > now
                || p.valid_until <= now || p.valid_until <= p.checked_at || p.valid_until > p.checked_at + c.market_quote_ttl_seconds
                || now - p.checked_at >= c.market_quote_ttl_seconds) return false;
            if (p.coverage_version == 2 && p.listing_observed < p.listing_total) return false;
            if (p.coverage_version == 3 && (!SellingStateProof.TryBest(p.best_state_proof,p.market_hash_name,out var best)
                || best.total != p.listing_total || best.remaining != p.best_remaining_breeds || best.maximum != p.best_maximum_breeds
                || best.price != p.seller_proceeds_krw)) return false;
            if (p.no_listings ? p.listing_total != 0 : p.listing_total < 1 || p.best_remaining_breeds < 0
                || p.best_maximum_breeds < 1 || p.best_remaining_breeds > p.best_maximum_breeds
                || !double.IsFinite(p.seller_proceeds_krw) || p.seller_proceeds_krw < 0) return false;
            var entry = catalog.SingleOrDefault(e => e.species == p.species);
            return entry != null && !entry.seasonal && entry.level == f.level && entry.market_names?.Length == 5
                && entry.market_names[f.grade - 1] == p.market_hash_name;
        }).ToArray();
        var expected = catalog.Where(e => !e.seasonal && e.level == f.level && !NewCohort(e.species) && !discovered.Contains(e.species)
            && e.market_names?.Length == 5).Select(e => e.market_names[f.grade-1]).Distinct();
        if (expected.Any(name => !valid.Any(p => p.market_hash_name == name))) return false;
        valid = valid.Where(p => !p.no_listings).ToArray();
        if (valid.Length == 0) return false;
        double price = valid.Max(p => p.seller_proceeds_krw);
        f.market_state = f.market_merge_state = price <= c.market_min_seller_proceeds_krw ? "release" : "keep";
        f.market_valid_until = f.market_merge_valid_until = valid.Min(p => p.valid_until);
        f.market_value_source = Source;
        f.market_estimated_seller_proceeds_krw = price;
        f.market_estimate_peer_count = valid.Length;
        return true;
    }

    static HashSet<string> VerifiedNewSpecies(SeasonDiscoveryEvidence evidence, int currentSeason, MarketCatalogEntry[] catalog, double now)
    {
        var result = new HashSet<string>();
        var a = evidence?.announcement;
        if (evidence == null || evidence.state != "verified" || currentSeason < 1 || evidence.season != currentSeason
            || a == null || a.season != currentSeason || string.IsNullOrEmpty(a.gid) || !a.gid.All(char.IsDigit)
            || !double.IsFinite(a.published_at) || a.published_at <= 0 || a.published_at > now
            || !Uri.TryCreate(a.url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "steamcommunity.com"
            || url.AbsolutePath != "/games/4842670/announcements/detail/" + a.gid || !url.IsDefaultPort
            || catalog == null || evidence.new_species == null || evidence.new_regular_species == null) return result;
        foreach (var item in evidence.new_species)
        {
            if (item == null || item.seasonal || item.identification != "native_catalog_delta" || !evidence.new_regular_species.Contains(item.species)) continue;
            var entry = catalog.SingleOrDefault(e => e.species == item.species);
            if (entry != null && !entry.seasonal && entry.market_names?.Length == 5 && entry.market_names[0] == item.name + " ★") result.Add(item.species);
        }
        return result;
    }
}
