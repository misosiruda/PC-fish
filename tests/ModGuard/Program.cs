using PCFish.Automation;

int checkedCases = 0;
Settings Settings() => new() { enabled = true, mode = "protected" };
FishState Fish(string id) => new() { id = id, species = "ordinary", level = 1, grade = 1, remaining_breeds = 1, ready = true };
Snapshot Snapshot() => new() { hearts = 1, capacity = 999, fish = [Fish("a"), Fish("b")] };
Command Breed() => new() { request_id = Guid.NewGuid().ToString(), kind = "breed", fish_ids = ["a", "b"] };
void Reject(Action change, Command command, Snapshot snapshot, Settings settings)
{
    change();
    try { ExecutionGuard.Validate(command, snapshot, settings); }
    catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { checkedCases++; return; }
    throw new Exception("Unsafe action accepted");
}
ExecutionGuard.Validate(Breed(), Snapshot(), Settings()); checkedCases++;
foreach (var field in new[] { "paused", "observe", "hearts", "capacity", "busy", "uncertain", "pending", "locked", "seasonal", "grade", "reserve", "species", "cooldown", "uses", "duplicate", "missing", "count", "unsupported", "immature" })
{
    var c = Breed(); var s = Snapshot(); var cfg = Settings();
    Reject(() => {
        switch(field)
        {
            case "paused": cfg.enabled = false; break;
            case "observe": cfg.mode = "observe"; break;
            case "hearts": s.hearts = 0; break;
            case "capacity": s.capacity = 2; break;
            case "busy": s.ui_busy = true; break;
            case "uncertain": s.uncertain = true; break;
            case "pending": s.request_in_flight = true; break;
            case "locked": s.fish[0].locked = true; break;
            case "seasonal": s.fish[0].seasonal = true; break;
            case "grade": s.fish[0].grade = 5; break;
            case "reserve": cfg.reserved_ids = ["a"]; break;
            case "species": cfg.reserved_species = ["ordinary"]; break;
            case "cooldown": s.fish[0].ready = false; break;
            case "uses": s.fish[0].remaining_breeds = 0; break;
            case "duplicate": c.fish_ids = ["a", "a"]; break;
            case "missing": c.fish_ids = ["a", "missing"]; break;
            case "count": c.fish_ids = ["a"]; break;
            case "unsupported": c.kind = "sell"; break;
            case "immature": s.fish[0].basic = true; s.fish[0].species = "FS00000"; s.fish[0].growth = 9; break;
        }
    }, c, s, cfg);
}
var materials = Enumerable.Range(0, 10).Select(i => Fish(i.ToString())).ToArray();
foreach (var f in materials) f.remaining_breeds = 0;
var merge = new Command { request_id = Guid.NewGuid().ToString(), kind = "merge", fish_ids = materials.Select(f => f.id).ToArray() };
var full = new Snapshot { hearts = 0, capacity = 10, fish = materials };
ExecutionGuard.Validate(merge, full, Settings()); checkedCases++;
Reject(() => materials[0].remaining_breeds = 1, merge, full, Settings());
materials[0].remaining_breeds = 0;
Reject(() => materials[0].basic = true, merge, full, Settings());

Snapshot LevelState() => new() { tank_level = 2, tank_exp = 150, next_level_exp = 100, max_tank_level = 50,
    level_up_available = true, home_menu = true, ui_busy = true };
void Check(bool pass, string scenario)
{
    if (!pass) throw new Exception("Level-up guard failed: " + scenario);
    checkedCases++;
}
Check(LevelUpGuard.CanUpgrade(Settings(), LevelState()), "upgrade while Home panel is open");
var econ = Settings(); econ.mode = "economic";
Check(LevelUpGuard.CanUpgrade(econ, LevelState()), "economic mode also levels");
foreach (var field in new[] { "paused", "observe", "disabled", "trade", "network", "confirmation", "pending", "uncertain", "popup", "xp", "max", "unavailable", "missing_threshold" })
{
    var s = LevelState(); var cfg = Settings();
    switch(field)
    {
        case "paused": cfg.enabled = false; break;
        case "observe": cfg.mode = "observe"; break;
        case "disabled": cfg.auto_level_up = false; break;
        case "trade": s.home_menu = false; break;
        case "network": s.network_busy = true; break;
        case "confirmation": s.confirmation_open = true; break;
        case "pending": s.request_in_flight = true; break;
        case "uncertain": s.uncertain = true; break;
        case "popup": s.level_up_popup_open = true; break;
        case "xp": s.tank_exp = 99; break;
        case "max": s.tank_level = 50; break;
        case "unavailable": s.level_up_available = false; break;
        case "missing_threshold": s.next_level_exp = 0; break;
    }
    Check(!LevelUpGuard.CanUpgrade(cfg, s), field);
}
var pop = LevelState(); pop.level_up_popup_open = true;
Check(LevelUpGuard.CanHandle(Settings(), pop) && !LevelUpGuard.CanUpgrade(Settings(), pop), "dismiss level reward without replaying upgrade");
pop.confirmation_open = true;
Check(!LevelUpGuard.CanHandle(Settings(), pop), "never dismiss unrelated confirmation");
var exactXp = LevelState(); exactXp.tank_exp = exactXp.next_level_exp;
Check(LevelUpGuard.CanUpgrade(Settings(), exactXp), "exact cumulative XP threshold");
LevelUpGuard.Confirm(2, 150, 50, 3, 150); checkedCases++;
foreach (var bad in new[] { (After: 2, Exp: 150), (After: 4, Exp: 150), (After: 3, Exp: 50) })
{
    bool rejected = false;
    try { LevelUpGuard.Confirm(2, 150, 50, bad.After, bad.Exp); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "detect skipped/duplicate/XP-changing level result");
}
var atMax = LevelState(); atMax.tank_level = atMax.max_tank_level;
Check(!LevelUpGuard.CanUpgrade(Settings(), atMax), "no repeated upgrade at cap");

// Regression: after closing Breed/Trade/etc, the last selected menu stays set.
// Closed windows must allow both breeding and auto-leveling despite that value.
foreach(var lastTab in new[] { "Home", "Collection", "Breed", "Merge", "Trade", "Shop", "Setting" })
{
    var context = UiActivityGuard.IsHomeContext(false, lastTab == "Home");
    var reason = UiActivityGuard.BlockReason(false, false, false, false, false);
    var s = Snapshot(); s.menu_name = lastTab; s.home_menu = context; s.ui_busy = reason.Length != 0;
    ExecutionGuard.Validate(Breed(), s, Settings()); checkedCases++;
    var level = LevelState(); level.home_menu = context; level.ui_busy = reason.Length != 0;
    Check(LevelUpGuard.CanUpgrade(Settings(), level), "closed " + lastTab + " tab does not prevent leveling");
}
foreach(var reason in new[] {
    UiActivityGuard.BlockReason(true, false, false, false, false),
    UiActivityGuard.BlockReason(false, true, false, false, false),
    UiActivityGuard.BlockReason(false, false, true, false, false),
    UiActivityGuard.BlockReason(false, false, false, true, false),
    UiActivityGuard.BlockReason(false, false, false, false, true) })
{
    Check(reason.Length > 0, "real activity has an explicit block reason");
    var s = Snapshot(); s.ui_busy = true;
    Reject(() => {}, Breed(), s, Settings());
}
Check(!UiActivityGuard.IsHomeContext(true, false), "visible non-Home menu blocks leveling");
var overlay = LevelState(); overlay.overlay_open = true;
Check(!LevelUpGuard.CanUpgrade(Settings(), overlay), "visible overlay blocks leveling");

// Regression: invisible native operations must update the aquarium heart HUD,
// including restoring a stale 4-heart display when authoritative data is zero.
var indicators = new TankIndicatorSynchronizer();
int authoritativeHearts = 0, renderedHearts = 4, refreshes = 0;
void Redraw() { renderedHearts = authoritativeHearts; refreshes++; }
Check(indicators.Sync(authoritativeHearts, 4, 450, Redraw) && renderedHearts == 0 && authoritativeHearts == 0,
    "stale four-heart display repairs to real zero without changing data");
Check(!indicators.Sync(0, 4, 450, Redraw) && refreshes == 1, "unchanged HUD does not redraw every frame");
foreach (int hearts in new[] { 1, 0, 2, 5, 4, 0 })
{
    authoritativeHearts = hearts;
    Check(indicators.Sync(hearts, 4, 450, Redraw) && renderedHearts == hearts && indicators.LastHearts == hearts,
        "refill/spend transition redraws authoritative hearts " + hearts);
}
Check(indicators.Sync(0, 4, 760, Redraw), "XP change refreshes level-up indicator");
Check(indicators.Sync(0, 5, 760, Redraw), "level change refreshes tank indicators");
int beforeFailure = refreshes;
try { indicators.Sync(1, 5, 760, () => throw new IOException("UI not ready")); }
catch (IOException) { }
Check(indicators.LastHearts == 0 && refreshes == beforeFailure, "failed redraw is not cached as synchronized");
authoritativeHearts = 1;
Check(indicators.Sync(1, 5, 760, Redraw) && renderedHearts == 1, "retry repairs HUD when native view becomes ready");
Console.WriteLine($"PASS {checkedCases} native execution guard cases");
