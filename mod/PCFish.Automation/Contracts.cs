namespace PCFish.Automation;

internal static class ExecutionGuard
{
    internal static void Validate(Command cmd, Snapshot s, Settings c)
    {
        c.Validate();
        if (!c.enabled || c.mode == "observe") throw new InvalidDataException("Automation paused");
        if (!Guid.TryParse(cmd.request_id, out _) || cmd.fish_ids == null || cmd.fish_ids.Distinct().Count() != cmd.fish_ids.Length) throw new InvalidDataException("Invalid request identity");
        if (s.ui_busy || s.uncertain || s.request_in_flight) throw new InvalidDataException("Game busy");
        var selected = cmd.fish_ids.Select(id => s.fish.Single(f => f.id == id)).ToArray();
        var seasonPlan = SeasonPolicy.Build(s, c);
        if (seasonPlan.state == "unavailable") throw new InvalidDataException(seasonPlan.reason);
        if (cmd.kind == "craft")
        {
            SeasonPolicy.ValidateCraft(cmd, s, c, seasonPlan);
            return;
        }
        if (cmd.kind == "merge" && selected.Any(f => seasonPlan.reserved_ids.Contains(f.id)))
            throw new InvalidDataException("Season recipe material reserved for crafting");
        if (selected.Any(f => f.locked || f.seasonal || (c.protection_mode == "market"
                ? !f.basic && (cmd.kind == "merge" && c.UsesProductMergeValue
                    ? f.market_merge_state != "release" || f.market_merge_valid_until <= s.captured_at
                    : f.market_state != "release" || f.market_valid_until <= s.captured_at)
                : f.grade >= c.protect_grade_at_least) || c.reserved_ids.Contains(f.id) || c.reserved_species.Contains(f.species)))
            throw new InvalidDataException("Protected fish selected");
        if (cmd.kind == "breed")
        {
            if (!c.auto_breed || selected.Length != 2 || s.hearts <= 0 || s.fish.Length >= s.capacity || selected.Any(f => !f.ready || (f.basic ? f.species != "FS00000" || f.growth < 10 : f.remaining_breeds < 1))) throw new InvalidDataException("Breeding conditions changed");
        }
        else if (cmd.kind == "merge")
        {
            if (!c.auto_merge || selected.Length != 10 || selected.Any(f => f.basic || f.level < 1)) throw new InvalidDataException("Merge conditions changed");
            if (!MergePolicy.Allows(selected))
                throw new InvalidDataException("Merge requires same stars, adjacent rarities and no downgrade risk");
            if (!StockPolicy.AllowsMerge(selected, s, c, seasonPlan))
                throw new InvalidDataException("Merge must preserve stock and parents, target missing materials or meet per-species triple surplus; exhausted fish first");
        }
        else throw new InvalidDataException("Unsupported action");
    }

}

internal static class MergePolicy
{
    internal static bool Allows(FishState[] selected)
    {
        if (selected.Length != 10 || selected.Any(f => f.level is < 1 or > 5 || f.grade is < 1 or > 5)) return false;
        var highest = selected.Max(f => f.level);
        var lowest = selected.Min(f => f.level);
        var minimumForecast = Math.Min(5, selected.Sum(f => f.level) / 10 + 1);
        return selected.All(f => f.grade == selected[0].grade) && highest - lowest <= 1 && minimumForecast >= highest;
    }
}

internal sealed class TankIndicatorSynchronizer
{
    public int LastHearts { get; private set; } = -1;
    int lastLevel = -1, lastExp = -1;

    // Rendering follows authoritative game data. This never changes resources.
    // Commit the cache only after a successful native UI refresh.
    internal bool Sync(int hearts, int level, int exp, Action refresh)
    {
        if (hearts == LastHearts && level == lastLevel && exp == lastExp) return false;
        refresh();
        LastHearts = hearts; lastLevel = level; lastExp = exp;
        return true;
    }
}

internal static class UiActivityGuard
{
    // currentMenuType remembers the last tab even after its window is closed.
    // Menus and lists are observation surfaces, not evidence of a transaction.
    internal static bool IsHomeContext(bool windowOpen, bool selectedHome) => !windowOpen || selectedHome;
    internal static string BlockReason(bool networkBusy, bool confirmationOpen, bool levelPopupOpen, bool windowOpen, bool overlayOpen)
        => networkBusy ? "Waiting for game network operation"
        : confirmationOpen ? "Waiting for confirmation dialog"
        : levelPopupOpen ? "Waiting for level-up reward popup"
        : "";
}

internal sealed class NetworkUiLease
{
    public string RequestId { get; private set; } = "";
    bool priorProcessing, priorVisible;
    internal void Begin(string requestId, bool processing, bool visible)
    {
        if (string.IsNullOrWhiteSpace(requestId) || RequestId.Length != 0)
            throw new InvalidOperationException("Network UI ownership is already active or invalid");
        RequestId = requestId; priorProcessing = processing; priorVisible = visible;
    }
    internal bool Release(string requestId, Action<bool, bool> restore)
    {
        if (RequestId.Length == 0 || requestId != RequestId) return false;
        RequestId = "";
        restore(priorProcessing, priorVisible);
        return true;
    }
}

internal static class LevelUpGuard
{
    // Observation menus may stay open. Confirmations and outstanding operations
    // remain interlocked; the specific level reward popup can be handled here.
    internal static bool CanHandle(Settings c, Snapshot s) => c.enabled && c.auto_level_up && c.mode is "protected" or "economic"
        && !s.network_busy && !s.confirmation_open && !s.request_in_flight && !s.uncertain;

    internal static bool CanUpgrade(Settings c, Snapshot s) => CanHandle(c, s) && !s.level_up_popup_open
        && s.level_up_available && s.tank_level >= 1 && s.tank_level < s.max_tank_level
        && s.next_level_exp > 0 && s.tank_exp >= s.next_level_exp;

    internal static bool Confirm(Snapshot before, Snapshot after)
    {
        // XP, growth, reward presentation and placement are observations, not
        // predictions we require the game to satisfy. Level progress is enough.
        // Only actual resource loss or removal of a user's lock is a hard stop.
        foreach (var fish in before.fish)
        {
            var current = after.fish.FirstOrDefault(f => f.id == fish.id);
            if (current == null || !fish.basic && current.remaining_breeds < fish.remaining_breeds
                || fish.locked && !current.locked)
                throw new UnexpectedConsumptionException("Inventory, breeding uses or a protected lock were lost during leveling");
        }
        if (after.hearts < before.hearts)
            throw new UnexpectedConsumptionException("Hearts were consumed during leveling");
        return after.tank_level > before.tank_level;
    }
}

internal sealed class UnexpectedConsumptionException(string message) : Exception(message);

internal static class RequestOutcomeGuard
{
    // An explicit server rejection is complete only when no child was returned
    // and a fresh inventory confirms no consumption. Natural feeding, timers,
    // heart recovery and display changes do not make a rejection ambiguous.
    internal static bool CanConfirmRejection(bool explicitFailure, string childId, Snapshot before, Snapshot after)
    {
        return explicitFailure && string.IsNullOrEmpty(childId) && HasNoConsumption(before, after);
    }

    internal static bool HasNoConsumption(Snapshot before, Snapshot after)
    {
        if (before.fish.Length == 0 || before.fish.Length != after.fish.Length || after.hearts < before.hearts) return false;
        var current = new Dictionary<string, FishState>();
        foreach (var fish in after.fish)
            if (string.IsNullOrEmpty(fish.id) || !current.TryAdd(fish.id, fish)) return false;
        var seen = new HashSet<string>();
        foreach (var fish in before.fish)
        {
            if (!seen.Add(fish.id) || !current.TryGetValue(fish.id, out var other)
                || fish.species != other.species || fish.variant != other.variant || fish.grade != other.grade
                || fish.level != other.level || fish.basic != other.basic || fish.seasonal != other.seasonal
                || fish.maximum_breeds != other.maximum_breeds || fish.remaining_breeds != other.remaining_breeds
                || fish.locked && !other.locked) return false;
        }
        return true;
    }
}

internal sealed class OptionalFeature
{
    public bool Paused { get; private set; }
    public string Note { get; private set; } = "";
    internal void Pause(string note) { Paused = true; Note = note; }
    // Pending optional work never becomes a pending fish consumption request.
    internal void Restore(bool pending) { if (pending) Pause("Previous level-up attempt retained; other automation continues"); }
    internal bool Run(Action action)
    {
        if (Paused) return false;
        try { action(); }
        catch (UnexpectedConsumptionException) { throw; }
        catch (Exception ex) { Pause(ex.GetType().Name + ": " + ex.Message); }
        return true;
    }
}

internal sealed class Settings
{
    public bool enabled { get; set; }
    public string mode { get; set; } = "observe";
    public bool auto_breed { get; set; } = true;
    public bool auto_merge { get; set; } = true;
    public bool auto_level_up { get; set; } = true;
    public bool auto_season_craft { get; set; }
    public string season_goal { get; set; } = "collection";
    public int season_target_grade { get; set; } = 5;
    public int season_target_count { get; set; } = 1;
    public string[] season_species_priority { get; set; } = ["FS00040", "FS00039", "FS00038"];
    public int protect_grade_at_least { get; set; } = 3;
    public string protection_mode { get; set; } = "grade";
    public string market_merge_price_basis { get; set; } = "exact_breeds";
    internal bool UsesProductMergeValue => market_merge_price_basis is "full_breeds" or "best_observed_breeds" or "highest_buy_order";
    public double market_min_seller_proceeds_krw { get; set; } = 1000;
    public double market_quote_ttl_seconds { get; set; } = 86400;
    public double market_request_interval_seconds { get; set; } = 120;
    public string[] reserved_ids { get; set; } = [];
    public string[] reserved_species { get; set; } = [];
    public double minimum_action_interval_seconds { get; set; } = 30;
    public string python_path { get; set; } = "";
    public string project_root { get; set; } = "";
    public void Validate()
    {
        if (mode is not ("observe" or "protected" or "economic") || protect_grade_at_least is < 2 or > 5 || !double.IsFinite(minimum_action_interval_seconds) || minimum_action_interval_seconds < 5 || reserved_ids == null || reserved_species == null
            || season_goal is not ("highest" or "collection" or "repeat") || season_target_grade is < 1 or > 5 || season_target_count is < 1 or > 10
            || season_species_priority == null || season_species_priority.Length > 100
            || season_species_priority.Any(string.IsNullOrWhiteSpace) || season_species_priority.Distinct().Count() != season_species_priority.Length
            || protection_mode is not ("grade" or "market") || market_merge_price_basis is not ("exact_breeds" or "full_breeds" or "best_observed_breeds" or "highest_buy_order")
            || !double.IsFinite(market_min_seller_proceeds_krw) || market_min_seller_proceeds_krw <= 0
            || !double.IsFinite(market_quote_ttl_seconds) || market_quote_ttl_seconds is < 60 or > 86400
            || !double.IsFinite(market_request_interval_seconds) || market_request_interval_seconds is < 30 or > 3600)
            throw new InvalidDataException("Invalid automation settings");
    }
}
internal sealed class FishState
{
    public string id { get; set; } = "";
    public string species { get; set; } = "";
    public string variant { get; set; } = "";
    public int level { get; set; }
    public int grade { get; set; }
    public int remaining_breeds { get; set; }
    public int maximum_breeds { get; set; }
    public string market_hash_name { get; set; } = "";
    public string market_state { get; set; } = "pending";
    public double market_valid_until { get; set; }
    public string market_merge_state { get; set; } = "pending";
    public double market_merge_valid_until { get; set; }
    public double? market_best_seller_proceeds_krw { get; set; }
    public double? market_seller_proceeds_krw { get; set; }
    public double? market_highest_buy_order_krw { get; set; }
    public long market_buy_order_count { get; set; }
    public string market_value_source { get; set; } = "unavailable";
    public double? market_estimated_seller_proceeds_krw { get; set; }
    public int market_estimate_peer_count { get; set; }
    public bool market_book_complete { get; set; }
    public int growth { get; set; }
    public double ready_at { get; set; }
    public bool ready { get; set; }
    public bool basic { get; set; }
    public bool locked { get; set; }
    public bool seasonal { get; set; }
    public bool placed { get; set; }
    public object Traits(double? readyAfter = null) => new { species, level, grade, remaining_breeds, unlimited_breeding = basic, ready_after_seconds = readyAfter ?? 0 };
}
internal sealed class Snapshot
{
    public string session { get; set; } = "";
    public string revision { get; set; } = "";
    public string config_digest { get; set; } = "";
    public double captured_at { get; set; }
    public double next_heart_at { get; set; }
    public int hearts { get; set; }
    public int capacity { get; set; }
    public bool ui_busy { get; set; }
    public bool request_in_flight { get; set; }
    public bool uncertain { get; set; }
    public int tank_level { get; set; }
    public int tank_exp { get; set; }
    public int next_level_exp { get; set; }
    public int max_tank_level { get; set; }
    public int max_tank_exp { get; set; }
    public string[] next_level_reward_fish_ids { get; set; } = [];
    public bool level_up_available { get; set; }
    public bool home_menu { get; set; }
    public bool breed_retry_after_server_restore { get; set; }
    public bool network_busy { get; set; }
    public bool ui_is_processing { get; set; }
    public bool network_indicator_visible { get; set; }
    public bool confirmation_open { get; set; }
    public bool level_up_popup_open { get; set; }
    public bool window_open { get; set; }
    public bool overlay_open { get; set; }
    public string menu_name { get; set; } = "";
    public string ui_block_reason { get; set; } = "";
    public FishState[] fish { get; set; } = [];
    public MarketCatalogEntry[] market_catalog { get; set; } = [];
    public SpeciesInfo[] species_catalog { get; set; } = [];
    public SeasonState season { get; set; } = null!;
}
internal sealed class Command
{
    public string request_id { get; set; } = "";
    public string session { get; set; } = "";
    public string revision { get; set; } = "";
    public string config_digest { get; set; } = "";
    public string kind { get; set; } = "";
    public string reason { get; set; } = "";
    public string[] fish_ids { get; set; } = [];
    public double expires_at { get; set; }
    public int season { get; set; }
    public string target_species { get; set; } = "";
    public int target_grade { get; set; }
}
