namespace PCFish.Automation;

internal sealed class SellingStatePrefix
{
    public int remaining_breeds { get; set; }
    public int listing_total { get; set; }
    public string query { get; set; } = "";
    public int sort_field { get; set; }
    public int sort_direction { get; set; }
    public double[] prefix_seller_prices { get; set; } = [];
    public int[] prefix_remaining_breeds { get; set; } = [];
    public int[] prefix_maximum_breeds { get; set; } = [];
    public string[] prefix_listing_ids { get; set; } = [];
}

internal sealed class SellingStateProof
{
    public int appid { get; set; }
    public int currency_id { get; set; }
    public string market_hash_name { get; set; } = "";
    public int total_before { get; set; }
    public int total_after { get; set; }
    public SellingStatePrefix[] states { get; set; } = [];

    internal sealed class Best
    {
        internal int remaining, maximum, listings, total;
        internal double price;
    }

    internal static bool TryBest(SellingStateProof proof, string name, out Best best)
    {
        best = new();
        if (proof == null || proof.appid != 4842670 || proof.currency_id != 16 || proof.market_hash_name != name
            || proof.total_before <= 0 || proof.total_after != proof.total_before || proof.states == null || proof.states.Length is < 1 or > 65) return false;
        long count = 0; bool found = false;
        for (int i = 0; i < proof.states.Length; i++)
        {
            var s = proof.states[i];
            if (s == null || s.remaining_breeds != i || s.query != $"\"Breeds: {i} /\"" || s.sort_field != 0 || s.sort_direction != 1
                || s.listing_total < 0 || s.prefix_seller_prices == null || s.prefix_remaining_breeds == null || s.prefix_maximum_breeds == null
                || s.prefix_seller_prices.Length != s.prefix_remaining_breeds.Length || s.prefix_seller_prices.Length != s.prefix_maximum_breeds.Length
                || s.prefix_listing_ids == null || s.prefix_listing_ids.Length != s.prefix_seller_prices.Length
                || s.prefix_listing_ids.Distinct().Count() != s.prefix_listing_ids.Length
                || s.prefix_seller_prices.Length > s.listing_total || (s.listing_total > 0) != (s.prefix_seller_prices.Length > 0)) return false;
            for (int j = 0; j < s.prefix_seller_prices.Length; j++)
                if (!double.IsFinite(s.prefix_seller_prices[j]) || s.prefix_seller_prices[j] < 0 || s.prefix_remaining_breeds[j] != i
                    || s.prefix_maximum_breeds[j] < Math.Max(1,i)) return false;
            count += s.listing_total;
            if (s.listing_total > 0)
            {
                found = s.prefix_seller_prices.Length == s.listing_total; best.remaining = i; best.maximum = s.prefix_maximum_breeds.Max(); best.price = s.prefix_seller_prices.Max();
                best.listings = s.listing_total; best.total = proof.total_before;
            }
        }
        return found && count == proof.total_before;
    }
}
