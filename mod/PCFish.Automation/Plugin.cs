using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NN.PF.Core;
using NN.PF.Core.Managers;
using NN.PF.Core.Network;
using NN.PF.Core.Util;
using NN.PF.Models;
using NN.PF.UI.Home;
using UnityEngine;

namespace PCFish.Automation;

[BepInPlugin("local.pcfish.automation", "PC FISH Automation", "0.3.0")]
public sealed class Plugin : BasePlugin
{
    internal static Plugin Current = null!;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    string root = "", configFile = "", session = Guid.NewGuid().ToString("N");
    double nextTick, dispatchedAt, nextActionAt;
    string message = "Starting", lastError = "", pendingId = "";
    bool uncertain, inFlight;
    int lastTankLevel = -1, lastTankExp = -1, levelUpCount;
    readonly TankIndicatorSynchronizer tankIndicators = new();
    readonly Dictionary<string, string> marketNames = new();
    Process worker;
    Il2CppSystem.Action<NetworkResult, bool, string> callback;

    public override void Load()
    {
        Current = this;
        root = Path.Combine(Paths.BepInExRootPath, "PCFishAutomation");
        Directory.CreateDirectory(root);
        configFile = Path.Combine(Paths.ConfigPath, "pcfish-automation.json");
        var binary = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
        using (var stream = File.OpenRead(binary))
        using (var hash = SHA256.Create())
        {
            if (Convert.ToHexString(hash.ComputeHash(stream)) != "F5F98F82FE2FED5F177B440DF07C9084C814EB8C5C9A141661A67FF184149F93")
            {
                message = "Game updated: bindings need reinspection";
                Status("unsupported_build");
                Log.LogError(message);
                return;
            }
        }
        uncertain = File.Exists(Path.Combine(root, "pending.json")) || File.Exists(Path.Combine(root, "level-up-pending.json"));
        AddComponent<Driver>();
        Log.LogInfo($"Automation loaded; local status: {root}. No input hooks.");
    }

    internal static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    internal static void Atomic(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temp, path, true); break; }
            catch (IOException) when (attempt < 19) { System.Threading.Thread.Sleep(5); }
            catch (UnauthorizedAccessException) when (attempt < 19) { System.Threading.Thread.Sleep(5); }
        }
    }
    void Status(string state, object extra = null) => Atomic(Path.Combine(root, "status.json"), new
    { version = "0.3.0", session, captured_at = Now, state, message, in_flight = inFlight, uncertain, pending_id = pendingId,
        heart_indicator_synced_to = tankIndicators.LastHearts,
        tank_level = lastTankLevel, tank_exp = lastTankExp, automatic_level_ups = levelUpCount, worker_running = worker is { HasExited: false }, extra });

    internal void Tick()
    {
        if (Now < nextTick) return;
        nextTick = Now + 1;
        try
        {
            if (!File.Exists(configFile)) { message = "Configuration missing; paused"; Status("paused"); return; }
            var raw = ReadText(configFile);
            var cfg = JsonSerializer.Deserialize<Settings>(raw) ?? throw new InvalidDataException("Empty settings");
            cfg.Validate();
            EnsureWorker(cfg);
            var data = UnityEngine.Object.FindObjectOfType<GameDataManager>();
            var game = UnityEngine.Object.FindObjectOfType<GameManager>();
            var ui = UnityEngine.Object.FindObjectOfType<UIManager>();
            if (data == null || game == null || ui == null || data.fishList == null || data.fishList.Count < 2)
            { message = "Waiting for loaded aquarium"; Status("loading"); return; }
            RefreshTankIndicators(data, game);
            if (inFlight && Now - dispatchedAt > 60) { uncertain = true; message = "Request timed out; manual reconciliation required"; }
            var snapshot = Capture(data, ui, Digest(raw), cfg);
            Atomic(Path.Combine(root, "snapshot.json"), snapshot);
            if (!cfg.enabled || cfg.mode == "observe") { message = "Observation mode"; Status("paused", new { fish = snapshot.fish.Length }); return; }
            if (uncertain) { Status("uncertain"); return; }
            if (TryAutoLevelUp(snapshot, cfg, data, game, ui)) return;
            if (inFlight || snapshot.ui_busy || Now < nextActionAt)
            {
                message = inFlight ? "Waiting for confirmed game result" : snapshot.ui_busy ? snapshot.ui_block_reason : "Waiting for action interval";
                Status("waiting"); return;
            }
            if (TryMarketLock(snapshot, cfg, data, game, ui)) return;
            var commandPath = Path.Combine(root, "command.json");
            if (!File.Exists(commandPath)) { message = "Automation active; awaiting planner's next action"; Status("ready"); return; }
            var cmd = JsonSerializer.Deserialize<Command>(ReadText(commandPath));
            File.Delete(commandPath);
            if (cmd == null) return;
            if (cmd.session != session || cmd.revision != snapshot.revision || cmd.config_digest != snapshot.config_digest || cmd.expires_at < Now || cmd.expires_at > Now + 15)
            { message = "Discarded stale recommendation"; Status("ready"); return; }
            ExecutionGuard.Validate(cmd, snapshot, cfg);
            if (File.Exists(Path.Combine(root, "events", cmd.request_id + ".json"))) throw new InvalidDataException("Request already completed");
            Dispatch(cmd, snapshot, data, game, cfg);
        }
        catch (Exception ex)
        {
            // Do not log network payloads, account identifiers or game secrets.
            var error = ex.GetType().Name + ": " + ex.Message;
            if (error != lastError) { Log.LogWarning(error); lastError = error; }
            message = error;
            if (inFlight) uncertain = true;
            try { Status(uncertain ? "uncertain" : "error"); } catch { /* disk failure: no new dispatch */ }
        }
    }

    void EnsureWorker(Settings cfg)
    {
        if (worker is { HasExited: false }) return;
        if (!File.Exists(cfg.python_path) || !Directory.Exists(Path.Combine(cfg.project_root, "src", "pcfish_planner")))
            throw new InvalidDataException("Planner Python/project path is missing");
        var start = new ProcessStartInfo(cfg.python_path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cfg.project_root };
        start.Environment["PYTHONPATH"] = Path.Combine(cfg.project_root, "src");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in new[] { "-m", "pcfish_planner.bridge", "--runtime", root, "--config", configFile, "--pid", Environment.ProcessId.ToString(), "--session", session }) start.ArgumentList.Add(arg);
        worker = Process.Start(start) ?? throw new IOException("Planner worker failed to start");
    }

    Snapshot Capture(GameDataManager data, UIManager ui, string configDigest, Settings cfg)
    {
        var fishes = new List<FishState>();
        var types = CSVLoader.Instance.fishTypeList;
        MarketReport market = null;
        if (cfg.protection_mode == "market")
        {
            try
            {
                var path = Path.Combine(root, "market-review.json");
                if (File.Exists(path)) market = JsonSerializer.Deserialize<MarketReport>(ReadText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { /* Missing or malformed evidence leaves every ordinary fish protected. */ }
        }
        for (int i = 0; i < data.fishList.Count; i++)
        {
            var f = data.fishList[i];
            bool known = types != null && types.ContainsKey(f.Type);
            bool basic = f.Type == "FS00000";
            bool cool = GameUtil.IsFishCooltime(f.BreedDatetime, f.BreedNextDatetime);
            double readyAt = 0;
            if (cool)
            {
                readyAt = DateTimeOffset.TryParse(f.BreedNextDatetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt.ToUnixTimeSeconds() : Now + 86400;
            }
            var state = new FishState { id = f.Id, species = f.Type, variant = f.Fish, level = f.Level, grade = f.Grade,
                remaining_breeds = f.BreedCount, maximum_breeds = f.BreedMaxCount, growth = f.Growth, ready_at = readyAt,
                market_hash_name = cfg.protection_mode == "market" && known && !basic ? MarketName(f.Type, f.Grade) : "",
                ready = !cool && (basic ? f.Growth >= GameConfig.CF_FISH_GROWTH_MAX : f.BreedCount > 0),
                basic = basic, locked = f.IsLocked, seasonal = !known || types![f.Type].Season > 0, placed = f.IsPlaced };
            MarketProtection.Apply(state, market, cfg, session, configDigest, Now);
            fishes.Add(state);
        }
        var fish = fishes.OrderBy(f => f.id, StringComparer.Ordinal).ToArray();
        lastTankLevel = data.tankLevel; lastTankExp = data.tankExp;
        var levelTable = CSVLoader.Instance.tankLevelList;
        int maxLevel = GameConfig.CF_TANK_MAX_LEVEL;
        int nextLevel = Math.Min(data.tankLevel + 1, maxLevel);
        int nextExp = levelTable != null && levelTable.ContainsKey(nextLevel) ? levelTable[nextLevel].Exp : 0;
        bool canLevel = nextExp > 0 && data.IsCheckTankLevelUp();
        bool windowOpen = Visible(ui.viewWindow);
        bool overlayOpen = Visible(ui.viewCollectionView) || Visible(ui.viewListView) || Visible(ui.viewDecoList)
            || Visible(ui.viewDecoControl) || Visible(ui.viewFriendList) || Visible(ui.viewNewSeasonPopup);
        bool home = UiActivityGuard.IsHomeContext(windowOpen, ui.currentMenuType == MenuType.Home);
        bool networkBusy = ui.isProcessing || Visible(ui.viewNetwork);
        bool confirm = ui.uiConfirm != null && ui.uiConfirm.gameObject.activeInHierarchy;
        var levelPopup = UnityEngine.Object.FindObjectOfType<UILevelUp>();
        bool levelPopupOpen = levelPopup != null && levelPopup.gameObject.activeInHierarchy;
        string revision = Digest(JsonSerializer.Serialize(new { fish, hearts = data.breedCount, timestamp = data.breedTimestamp, level = data.tankLevel, exp = data.tankExp }));
        string blockReason = UiActivityGuard.BlockReason(networkBusy, confirm, levelPopupOpen, windowOpen, overlayOpen);
        bool busy = blockReason.Length != 0;
        return new Snapshot { session = session, revision = revision, config_digest = configDigest, captured_at = Now,
            fish = fish, hearts = data.breedCount, next_heart_at = data.breedTimestamp + GameConfig.CF_BREED_COUNT_INTERVAL,
            capacity = GameConfig.CF_FISH_MAX_COUNT, ui_busy = busy, request_in_flight = inFlight, uncertain = uncertain,
            tank_level = data.tankLevel, tank_exp = data.tankExp, next_level_exp = nextExp, max_tank_level = maxLevel,
            level_up_available = canLevel, home_menu = home, network_busy = networkBusy, confirmation_open = confirm, level_up_popup_open = levelPopupOpen,
            window_open = windowOpen, overlay_open = overlayOpen, menu_name = ui.currentMenuType.ToString(), ui_block_reason = blockReason };
    }

    static bool Visible(GameObject view) => view != null && view.activeInHierarchy;

    string MarketName(string species, int grade)
    {
        if (grade is < 1 or > 5) return "";
        if (!marketNames.TryGetValue(species, out var name))
        {
            string term = "Fish/" + species.ToLowerInvariant();
            name = I2.Loc.LocalizationManager.GetTranslation(term, true, 0, true, false, null, "English", true);
            if (string.IsNullOrWhiteSpace(name) || name == term || name.Contains('/')) return "";
            marketNames[species] = name;
        }
        return name + " " + new string('★', grade);
    }

    bool TryMarketLock(Snapshot before, Settings cfg, GameDataManager data, GameManager game, UIManager ui)
    {
        var keep = before.fish.FirstOrDefault(f => MarketProtection.CanLock(f, cfg, before));
        if (keep == null) return false;
        FishModel native = null;
        for (int i = 0; i < data.fishList.Count; i++) if (data.fishList[i].Id == keep.id) { native = data.fishList[i]; break; }
        // FishUtil.Lock toggles. Never call it on an already locked fish.
        if (native == null || !MarketProtection.EnsureLocked(() => native.IsLocked, () => FishUtil.Lock(native))) return false;
        var events = Path.Combine(root, "market-lock-events");
        Directory.CreateDirectory(events);
        Atomic(Path.Combine(events, Guid.NewGuid().ToString("N") + ".json"), new { kind = "market_lock", confirmed = true,
            completed_at = Now, fish_id = keep.id, keep.market_hash_name, keep.remaining_breeds, keep.maximum_breeds,
            seller_proceeds_krw = keep.market_seller_proceeds_krw, threshold_krw = cfg.market_min_seller_proceeds_krw });
        game.OnRefresh?.Invoke();
        Atomic(Path.Combine(root, "snapshot.json"), Capture(data, ui, before.config_digest, cfg));
        message = $"Locked valuable fish: {keep.market_hash_name}";
        nextActionAt = Now + 1;
        Status("market_locked");
        return true;
    }

    void RefreshTankIndicators(GameDataManager data, GameManager game)
        => tankIndicators.Sync(data.breedCount, data.tankLevel, data.tankExp, () => game.CheckTankShowPopup());

    bool TryAutoLevelUp(Snapshot before, Settings cfg, GameDataManager data, GameManager game, UIManager ui)
    {
        if (!LevelUpGuard.CanHandle(cfg, before)) return false;
        if (before.level_up_popup_open)
        {
            var popup = UnityEngine.Object.FindObjectOfType<UILevelUp>();
            if (popup == null) return false;
            popup.Ok(); // Inspected normal handler: dismiss level reward UI only.
            if (popup.gameObject.activeInHierarchy) throw new InvalidDataException("Level reward popup did not close");
            message = "Dismissed level-up reward popup";
            Log.LogInfo(message);
            Atomic(Path.Combine(root, "snapshot.json"), Capture(data, ui, before.config_digest, cfg));
            Status("ready");
            return true;
        }
        if (Now < nextActionAt || !LevelUpGuard.CanUpgrade(cfg, before) || !data.IsCheckTankLevelUp()) return false;
        var id = Guid.NewGuid().ToString("N");
        var journal = Path.Combine(root, "level-up-pending.json");
        var events = Path.Combine(root, "level-events");
        Directory.CreateDirectory(events);
        Atomic(journal, new { event_id = id, kind = "level_up", before, dispatched_at = Now });
        pendingId = id; inFlight = true; dispatchedAt = Now;
        message = $"Leveling tank from {before.tank_level}";
        Status("leveling");
        // This normal game method checks XP/max level, expands the tank, grants
        // the level's fish/deco rewards and updates achievements exactly once.
        data.UpgradeTankLevel();
        LevelUpGuard.Confirm(before.tank_level, before.tank_exp, before.max_tank_level, data.tankLevel, data.tankExp);
        data.SaveData();
        RefreshTankIndicators(data, game);
        game.OnRefresh?.Invoke();
        game.OnReddotStatus?.Invoke();
        var after = Capture(data, ui, before.config_digest, cfg);
        Atomic(Path.Combine(events, id + ".json"), new { event_id = id, kind = "level_up", confirmed = true, completed_at = Now,
            before_level = before.tank_level, after_level = after.tank_level, before_exp = before.tank_exp, after_exp = after.tank_exp,
            required_cumulative_exp = before.next_level_exp, before_fish_count = before.fish.Length, after_fish_count = after.fish.Length });
        File.Delete(journal);
        inFlight = false; pendingId = ""; levelUpCount++; nextActionAt = Now + 1;
        message = $"Confirmed tank level-up {before.tank_level} -> {after.tank_level}";
        Log.LogInfo(message);
        Atomic(Path.Combine(root, "snapshot.json"), Capture(data, ui, before.config_digest, cfg));
        Status("confirmed");
        return true;
    }

    void Dispatch(Command cmd, Snapshot before, GameDataManager data, GameManager game, Settings cfg)
    {
        // Persist BEFORE calling the game's network method. A restart cannot repeat an ambiguous request.
        Atomic(Path.Combine(root, "pending.json"), new { cmd, before, dispatched_at = Now });
        pendingId = cmd.request_id; inFlight = true; dispatchedAt = Now;
        message = $"{cmd.kind}: {cmd.reason}";
        Log.LogInfo($"Dispatch {cmd.kind} ({cmd.fish_ids.Length} fish), request {cmd.request_id}");
        Status("requesting");
        callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NetworkResult, bool, string>>(
            new Action<NetworkResult, bool, string>((result, isNew, childId) =>
            {
                try
                {
                    if (!inFlight || pendingId != cmd.request_id) return;
                    if (result != NetworkResult.Success) { uncertain = true; inFlight = false; message = $"{result}; stopped without retry"; Status("uncertain"); return; }
                    // FishBreed already applies server child/parents and saves. The normal UI success
                    // handler separately awards tank XP and spends exactly one heart; preserve that path.
                    if (cmd.kind == "breed")
                    {
                        int basicCount = cmd.fish_ids.Count(id => GameConfig.CF_FISH_BASIC_ID.Contains(id));
                        data.AddExp((2 - basicCount) * 10);
                        if (data.breedCount >= GameConfig.CF_BREED_MAX_COUNT) data.breedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        data.UseBreedCount();
                    }
                    data.SaveData();
                    var ui = UnityEngine.Object.FindObjectOfType<UIManager>();
                    ui.isProcessing = false;
                    // Normal UIBreed success calls CheckTankShowPopup after
                    // spending/saving. OnRefresh alone does not redraw the
                    // aquarium's UIBreedTankPopup heart sprites.
                    RefreshTankIndicators(data, game);
                    game.OnRefresh?.Invoke();
                    game.OnReddotStatus?.Invoke();
                    var after = Capture(data, ui, before.config_digest, cfg);
                    var child = after.fish.SingleOrDefault(f => f.id == childId);
                    if (child == null || before.fish.Any(f => f.id == childId)) throw new InvalidDataException("Success child was not a new inventory item");
                    var parents = cmd.fish_ids.Select(id => before.fish.Single(f => f.id == id)).ToArray();
                    if (cmd.kind == "merge" && cmd.fish_ids.Any(id => after.fish.Any(f => f.id == id))) throw new InvalidDataException("Merge material still present after success");
                    if (cmd.kind == "breed" && parents.Where(f => !f.basic).Any(p => after.fish.Single(f => f.id == p.id).remaining_breeds != p.remaining_breeds - 1)) throw new InvalidDataException("Parent count did not decrement exactly once");
                    var record = new { event_id = cmd.request_id, kind = cmd.kind, confirmed = true, synthetic = false, completed_at = Now,
                        parents = parents.Select(f => f.Traits()), result = child.Traits(Math.Max(0, child.ready_at - Now)), before_hearts = before.hearts, after_hearts = data.breedCount };
                    // One immutable event per request; the learner deduplicates IDs too.
                    Atomic(Path.Combine(root, "events", cmd.request_id + ".json"), record);
                    File.Delete(Path.Combine(root, "pending.json"));
                    inFlight = false; uncertain = false; pendingId = ""; nextActionAt = Now + cfg.minimum_action_interval_seconds;
                    message = $"Confirmed {cmd.kind}; result Level {child.level}, Grade {child.grade}";
                    Log.LogInfo(message);
                    Status("confirmed");
                }
                catch (Exception ex) { uncertain = true; inFlight = false; message = ex.GetType().Name + ": " + ex.Message; Log.LogError(message); Status("uncertain"); }
            }));
        Directory.CreateDirectory(Path.Combine(root, "events"));
        var network = UnityEngine.Object.FindObjectOfType<NetworkManager>();
        if (network == null) throw new InvalidDataException("Network manager unavailable");
        var ids = new Il2CppStringArray(cmd.fish_ids);
        if (cmd.kind == "breed") network.FishBreed(ids, callback); else network.FishMerge(ids, callback);
    }
}

public sealed class Driver(IntPtr pointer) : MonoBehaviour(pointer)
{
    public void Update() => Plugin.Current.Tick();
}
