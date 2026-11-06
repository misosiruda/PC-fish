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
        if (selected.Any(f => f.locked || f.seasonal || f.grade >= c.protect_grade_at_least || c.reserved_ids.Contains(f.id) || c.reserved_species.Contains(f.species)))
            throw new InvalidDataException("Protected fish selected");
        if (cmd.kind == "breed")
        {
            if (!c.auto_breed || selected.Length != 2 || s.hearts <= 0 || s.fish.Length >= s.capacity || selected.Any(f => !f.ready || (f.basic ? f.species != "FS00000" || f.growth < 10 : f.remaining_breeds < 1))) throw new InvalidDataException("Breeding conditions changed");
        }
        else if (cmd.kind == "merge")
        {
            if (!c.auto_merge || selected.Length != 10 || selected.Any(f => f.basic || f.level < 1 || f.remaining_breeds != 0)) throw new InvalidDataException("Merge conditions changed");
        }
        else throw new InvalidDataException("Unsupported action");
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
    // Only live visibility and actual transactions are evidence of interaction.
    internal static bool IsHomeContext(bool windowOpen, bool selectedHome) => !windowOpen || selectedHome;
    internal static string BlockReason(bool networkBusy, bool confirmationOpen, bool levelPopupOpen, bool windowOpen, bool overlayOpen)
        => networkBusy ? "Waiting for game network operation"
        : confirmationOpen ? "Waiting for confirmation dialog"
        : levelPopupOpen ? "Waiting for level-up reward popup"
        : windowOpen ? "Waiting for visible game menu to close"
        : overlayOpen ? "Waiting for visible game overlay to close"
        : "";
}

internal static class LevelUpGuard
{
    // A Home panel or the specific level reward popup may be open. Other
    // confirmations, menus and any outstanding operation remain interlocked.
    internal static bool CanHandle(Settings c, Snapshot s) => c.enabled && c.auto_level_up && c.mode is "protected" or "economic"
        && s.home_menu && !s.overlay_open && !s.network_busy && !s.confirmation_open && !s.request_in_flight && !s.uncertain;

    internal static bool CanUpgrade(Settings c, Snapshot s) => CanHandle(c, s) && !s.level_up_popup_open
        && s.level_up_available && s.tank_level >= 1 && s.tank_level < s.max_tank_level
        && s.next_level_exp > 0 && s.tank_exp >= s.next_level_exp;

    internal static void Confirm(int beforeLevel, int beforeExp, int maxLevel, int afterLevel, int afterExp)
    {
        // The inspected game uses cumulative XP; leveling never subtracts XP.
        if (beforeLevel >= maxLevel || afterLevel != beforeLevel + 1 || afterExp != beforeExp)
            throw new InvalidDataException("Level-up result did not match one normal cumulative-XP upgrade");
    }
}

internal sealed class Settings
{
    public bool enabled { get; set; }
    public string mode { get; set; } = "observe";
    public bool auto_breed { get; set; } = true;
    public bool auto_merge { get; set; } = true;
    public bool auto_level_up { get; set; } = true;
    public int protect_grade_at_least { get; set; } = 3;
    public string[] reserved_ids { get; set; } = [];
    public string[] reserved_species { get; set; } = [];
    public double minimum_action_interval_seconds { get; set; } = 10;
    public string python_path { get; set; } = "";
    public string project_root { get; set; } = "";
    public void Validate()
    {
        if (mode is not ("observe" or "protected" or "economic") || protect_grade_at_least is < 2 or > 5 || minimum_action_interval_seconds < 5 || reserved_ids == null || reserved_species == null)
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
    public bool level_up_available { get; set; }
    public bool home_menu { get; set; }
    public bool network_busy { get; set; }
    public bool confirmation_open { get; set; }
    public bool level_up_popup_open { get; set; }
    public bool window_open { get; set; }
    public bool overlay_open { get; set; }
    public string menu_name { get; set; } = "";
    public string ui_block_reason { get; set; } = "";
    public FishState[] fish { get; set; } = [];
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
}
