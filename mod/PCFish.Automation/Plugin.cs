using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using HarmonyLib;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NN.PF.Core;
using NN.PF.Core.Managers;
using NN.PF.Core.Network;
using NN.PF.Core.Util;
using NN.PF.Models;
using NN.PF.UI.Home;
using UnityEngine;

namespace PCFish.Automation;

[BepInPlugin("local.pcfish.automation", "PC FISH Automation", "0.5.0")]
public sealed partial class Plugin : BasePlugin
{
    internal static Plugin Current = null!;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    string root = "", configFile = "", session = Guid.NewGuid().ToString("N");
    double nextTick, dispatchedAt, nextActionAt;
    string message = "Starting", lastError = "", pendingId = "";
    bool uncertain, inFlight, bindingsValidated, transactionHooksReady;
    int lastTankLevel = -1, lastTankExp = -1, levelUpCount;
    readonly TankIndicatorSynchronizer tankIndicators = new();
    readonly NetworkUiLease requestUiLease = new();
    readonly OptionalFeature levelUpFeature = new();
    double nextDisplayRefresh, nextMarketLockAttempt;
    string displayNote = "", marketLockNote = "";
    string responseDiagnosticNote = "", pendingKind = "";
    Command dispatchedCommand;
    bool bindingNativeRequest;
    bool inventorySynced, inventorySyncing;
    double nextInventorySync;
    string inventorySyncNote = "Server inventory has not been reconciled";
    string inventoryAccountContext = "", syncingAccountContext = "";
    string[] serverInventoryIds;
    Il2CppSystem.Action<bool> inventorySyncCallback;
    bool probeBinding, probeInFlight;
    string probeId = "", probeContext = "", lastSavedInventory = "";
    Snapshot probeBefore;
    InventoryProbeTicket activeProbe;
    Il2CppSystem.Action<string> probeResponse;
    Il2CppSystem.Action probeError;
    Il2CppSystem.Action<bool> ignoredProbeCallback;
    Action<string[]> probeCompleted;
    bool probeWasProcessing, probeWasVisible, replayingReceipt;
    readonly ResponseBindings<NativeResponseBinding> responseBindings = new();
    readonly HashSet<string> observedChildReceipts = new(StringComparer.Ordinal);
    ReceiptCommit receipt;
    Action receiptContinuation;
    double nextReceiptCheck;
    Snapshot dispatchBefore;
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
        bindingsValidated = true;
        uncertain = File.Exists(Path.Combine(root, "pending.json")) || File.Exists(Path.Combine(root, "consumption-alert.json"));
        try { LoadNetworkRecovery(); LoadNativeBreedRetry(); }
        catch (Exception ex) { uncertain = true; message = "Recovery journal retained: " + ex.GetType().Name; }
        levelUpFeature.Restore(File.Exists(Path.Combine(root, "level-up-pending.json")));
        try
        {
            var harmony = new Harmony("local.pcfish.automation.response-diagnostics");
            foreach (var (type, method) in new[] { ("__c__DisplayClass21_0", "_FishBreed_b__0"), ("__c__DisplayClass22_0", "_FishMerge_b__0"), ("__c__DisplayClass23_0", "_FishCraft_b__0") })
            {
                var nested = typeof(NetworkManager).GetNestedType(type, BindingFlags.Public | BindingFlags.NonPublic);
                var target = nested?.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null) throw new MissingMethodException(type, method);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ObserveResponse), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            harmony.Patch(typeof(NANOOGame.Network.NetworkManager).GetMethod("RequestPut"),
                prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(BindRequest), BindingFlags.Static | BindingFlags.NonPublic)));
            var restore = typeof(NetworkManager).GetNestedType("__c__DisplayClass19_0", BindingFlags.Public | BindingFlags.NonPublic);
            harmony.Patch(restore.GetMethod("_ManageRestore_b__0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ObserveInventoryResponse), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(typeof(NANOOGame.Network.NetworkManager).GetMethod("RequestGet"),
                prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(InterceptProbeGet), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(typeof(GameDataManager).GetMethod("SaveData"),
                postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ObserveSave), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(typeof(GameDataManager).GetMethod("LoadData"),
                postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ObserveLoad), BindingFlags.Static | BindingFlags.NonPublic)));
            InstallNativeComparison(harmony);
            transactionHooksReady = true;
            responseDiagnosticNote = "Response diagnostics active";
        }
        catch (Exception ex) { responseDiagnosticNote = ex.GetType().Name; }
        try { InstallHttpDiagnostics(new Harmony("local.pcfish.automation.http-diagnostics")); }
        catch (Exception ex) { httpDiagnosticNote = "HTTP observation unavailable: " + ex.GetType().Name; }
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
    { version = "0.5.0", session, captured_at = Now, state, message, in_flight = inFlight, uncertain, pending_id = pendingId,
        native_breed_retry = nativeBreedRetry,
        native_comparison = nativeComparison,
        native_breed_flow = NativeBreedRequest ? recovery.flow : "",
        native_completed_at = NativeBreedRequest ? recovery.native_completed_at : 0,
        native_next_verification_at = NativeBreedRequest && recovery.native_completed_at > 0
            ? recovery.native_completed_at + (NativeComparisonRequest ? nativeComparison?.quiet_seconds ?? 60 : 60) : 0,
        network_ui_owner = requestUiLease.RequestId,
        deferred_receipts = DeferredReceiptCount(), deferred_receipt_note = deferredReceiptNote,
        inventory_next_request_at = inventoryPacing.next_at, inventory_backoff_failures = inventoryPacing.failures,
        recovery_request_id = recovery?.cmd.request_id ?? "", recovery_note = recovery?.note ?? "",
        response_diagnostic_note = responseDiagnosticNote,
        response_bindings_active = responseBindings.ActiveCount,
        response_bindings_settled = responseBindings.Count - responseBindings.ActiveCount,
        http_diagnostic_note = httpDiagnosticNote,
        inventory_synced = inventorySynced, inventory_syncing = inventorySyncing, inventory_sync_note = inventorySyncNote,
        account_context_hash = bindingsValidated ? AccountContext() : "", inventory_account_context_hash = inventoryAccountContext,
        receipt_note = receipt?.Note ?? "", receipt_server_confirmed = receipt?.Confirmed ?? false,
        level_up_paused = levelUpFeature.Paused, level_up_note = levelUpFeature.Note, display_note = displayNote, market_lock_note = marketLockNote,
        heart_indicator_synced_to = tankIndicators.LastHearts,
        tank_level = lastTankLevel, tank_exp = lastTankExp, automatic_level_ups = levelUpCount, worker_running = worker is { HasExited: false }, extra });

    internal void Tick()
    {
        if (Now < nextTick) return;
        nextTick = Now + 1;
        try
        {
            responseBindings.PruneSettled();
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
            if (inFlight && receipt == null && !NativeBreedRequest && Now - dispatchedAt > 60)
            { uncertain = true; message = "Waiting for read-only recovery of the outstanding request"; }
            var snapshot = Capture(data, ui, Digest(raw), cfg);
            Atomic(Path.Combine(root, "snapshot.json"), snapshot);
            if (!transactionHooksReady)
            { message = "Required inventory transaction hooks unavailable: " + responseDiagnosticNote; Status("unsupported_bindings"); return; }
            if (probeInFlight && Now - activeProbe.StartedAt > 15) FinishProbe(activeProbe, "", ui);
            if (probeInFlight) { message = "Reading server inventory without applying changes"; Status("waiting"); return; }
            if (TryNativeBreedPending(snapshot, data, game, ui, cfg)) return;
            if (TryNativeComparison(snapshot, data, game, ui, cfg)) return;
            if (receipt != null && !receipt.Applied && !(recovery?.validation_failed ?? false))
            {
                if (Now >= nextReceiptCheck && inventoryPacing.Ready(Now))
                {
                    nextReceiptCheck = inventoryPacing.next_at;
                    StartReadOnlyProbe(Guid.NewGuid().ToString(), snapshot, ui, ids => ConfirmReceipt(ids));
                }
                message = receipt.Note; Status("waiting_network"); return;
            }
            if (cfg.enabled && TryRecoverPending(snapshot, data, game, ui, cfg)) return;
            var probePath = Path.Combine(root, "inventory-probe.request.json");
            if (File.Exists(probePath) && !inFlight && !inventorySyncing && !snapshot.ui_busy && !uncertain)
            {
                if (!inventoryPacing.Ready(Now)) { message = "Waiting for inventory read interval"; Status("waiting_network"); return; }
                var request = JsonSerializer.Deserialize<Command>(ReadText(probePath));
                File.Delete(probePath);
                if (request != null && Guid.TryParse(request.request_id, out _)) StartReadOnlyProbe(request.request_id, snapshot, ui);
                return;
            }
            if (!cfg.enabled || cfg.mode == "observe") { message = "Observation mode"; Status("paused", new { fish = snapshot.fish.Length }); return; }
            if (uncertain) { Status("uncertain"); return; }
            if (TryCheckDeferredReceipts(snapshot, ui)) return;
            if (inventorySynced && inventoryAccountContext != AccountContext())
            {
                inventorySynced = false;
                inventorySyncNote = "Authenticated account changed; previous inventory context cannot be reused";
            }
            if (!inventorySynced)
            {
                if (!inventorySyncing && !inFlight && !snapshot.ui_busy && Now >= nextInventorySync && inventoryPacing.Ready(Now))
                    StartInventorySync(snapshot, data, game, ui, cfg);
                else { message = inventorySyncing ? "Reconciling local inventory with server" : "Waiting for inventory reconciliation"; Status("waiting"); }
                return;
            }
            if (TryAutoLevelUp(snapshot, cfg, data, game, ui)) return;
            if (inFlight || snapshot.ui_busy || Now < nextActionAt)
            {
                message = inFlight ? "Waiting for confirmed game result" : snapshot.ui_busy ? snapshot.ui_block_reason : "Waiting for action interval";
                Status("waiting"); return;
            }
            if (Now >= nextMarketLockAttempt)
            {
                try { if (TryMarketLock(snapshot, cfg, data, game, ui)) return; marketLockNote = ""; }
                catch (Exception ex)
                {
                    marketLockNote = ex.GetType().Name + ": " + ex.Message;
                    nextMarketLockAttempt = Now + 60;
                    message = "Automatic lock deferred; market protection remains active";
                    Status("waiting"); return;
                }
            }
            var commandPath = Path.Combine(root, "command.json");
            if (!File.Exists(commandPath)) { message = "Automation active; awaiting planner's next action"; Status("ready"); return; }
            var cmd = JsonSerializer.Deserialize<Command>(ReadText(commandPath));
            File.Delete(commandPath);
            if (cmd == null) return;
            if (cmd.session != session || cmd.revision != snapshot.revision || cmd.config_digest != snapshot.config_digest || cmd.expires_at < Now || cmd.expires_at > Now + 15)
            { message = "Discarded stale recommendation"; Status("ready"); return; }
            try { ExecutionGuard.Validate(cmd, snapshot, cfg); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            { message = "Replanning: " + ex.Message; Status("waiting"); return; }
            if (cmd.kind == "craft" && (CommonData.seasonId != cmd.season || !GameUtil.IsSeasonActive() || Now >= snapshot.season.ends_at))
                throw new InvalidDataException("Season ended before dispatch");
            if (new[] { "events", "craft-events", "rejected-events" }.Any(folder => File.Exists(Path.Combine(root, folder, cmd.request_id + ".json")))) throw new InvalidDataException("Request already completed");
            void AfterOwnershipCheck(string[] ids)
            {
                if (ids == null) { nextActionAt = Now + cfg.minimum_action_interval_seconds; message = "Waiting for server inventory verification"; Status("waiting"); return; }
                if (!ReceiptCommit.InputsOwned(cmd, snapshot, ids))
                {
                    inventorySynced = false; inventorySyncNote = "Selected local fish is absent from server inventory; no mutation sent";
                    message = inventorySyncNote; Status("waiting"); return;
                }
                try
                {
                    var currentRaw = ReadText(configFile);
                    var currentCfg = JsonSerializer.Deserialize<Settings>(currentRaw) ?? throw new InvalidDataException("Empty settings");
                    var current = Capture(data, ui, Digest(currentRaw), currentCfg);
                    if (cmd.config_digest != current.config_digest || cmd.expires_at < Now || cmd.revision != current.revision)
                    { message = "Replanning after inventory verification"; Status("waiting"); return; }
                    ExecutionGuard.Validate(cmd, current, currentCfg);
                    Dispatch(cmd, current, data, game, currentCfg);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                { message = "Replanning after inventory verification: " + ex.Message; Status("waiting"); }
            }
            if (cmd.kind == "breed" && cmd.fish_ids.All(id => snapshot.fish.Single(f => f.id == id).basic))
            {
                if (Now - inventoryPacing.last_completed_at < 60)
                { message = "Waiting for quiet interval after inventory read before starter breeding"; Status("waiting"); return; }
                AfterOwnershipCheck([]);
            }
            else if (InventoryRequestPacing.CanReuse(serverInventoryIds, inventoryAccountContext, AccountContext(), serverInventoryVerifiedAt, snapshot, Now))
                AfterOwnershipCheck(serverInventoryIds);
            else if (inventoryPacing.Ready(Now))
                StartReadOnlyProbe(Guid.NewGuid().ToString(), snapshot, ui, AfterOwnershipCheck);
            else { message = "Waiting for inventory read interval"; Status("waiting_network"); }
        }
        catch (Exception ex)
        {
            // Do not log network payloads, account identifiers or game secrets.
            var error = ex.GetType().Name + ": " + ex.Message;
            if (error != lastError) { Log.LogWarning(error); lastError = error; }
            message = error;
            if (inFlight) uncertain = true;
            if (ex is UnexpectedConsumptionException)
            {
                uncertain = true;
                Atomic(Path.Combine(root, "consumption-alert.json"), new { captured_at = Now, session, reason = error });
            }
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
        var catalog = new List<MarketCatalogEntry>();
        if (cfg.protection_mode == "market" && types != null)
            foreach (var row in types.Values)
                if (row.Level > 0)
                    catalog.Add(new() { species = row.Type, level = row.Level, seasonal = row.Season > 0,
                        market_names = Enumerable.Range(1, 5).Select(g => MarketName(row.Type, g)).ToArray() });
        var marketCatalog = catalog.ToArray();
        var speciesCatalog = new List<SpeciesInfo>();
        if (types != null)
            foreach (var row in types.Values)
                if (row.Level > 0) speciesCatalog.Add(new() { species = row.Type, name = FishName(row.Type), level = row.Level, seasonal = row.Season > 0 });
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
            MarketProtection.Apply(state, market, cfg, session, configDigest, Now, marketCatalog, CommonData.seasonId);
            fishes.Add(state);
        }
        var fish = fishes.OrderBy(f => f.id, StringComparer.Ordinal).ToArray();
        lastTankLevel = data.tankLevel; lastTankExp = data.tankExp;
        var levelTable = CSVLoader.Instance.tankLevelList;
        int maxLevel = GameConfig.CF_TANK_MAX_LEVEL;
        int nextLevel = Math.Min(data.tankLevel + 1, maxLevel);
        int nextExp = levelTable != null && levelTable.ContainsKey(nextLevel) ? levelTable[nextLevel].Exp : 0;
        string rewardFish = levelTable != null && levelTable.ContainsKey(nextLevel) ? levelTable[nextLevel].RewardFishId : null;
        string[] rewardIds = rewardFish == null ? null : rewardFish.Equals("none", StringComparison.OrdinalIgnoreCase) ? []
            : rewardFish.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool canLevel = nextExp > 0 && data.IsCheckTankLevelUp();
        bool windowOpen = Visible(ui.viewWindow);
        bool overlayOpen = Visible(ui.viewCollectionView) || Visible(ui.viewListView) || Visible(ui.viewDecoList)
            || Visible(ui.viewDecoControl) || Visible(ui.viewFriendList) || Visible(ui.viewNewSeasonPopup);
        bool home = UiActivityGuard.IsHomeContext(windowOpen, ui.currentMenuType == MenuType.Home);
        bool networkBusy = ui.isProcessing || Visible(ui.viewNetwork);
        bool confirm = ui.uiConfirm != null && ui.uiConfirm.gameObject.activeInHierarchy;
        var levelPopup = UnityEngine.Object.FindObjectOfType<UILevelUp>();
        bool levelPopupOpen = levelPopup != null && levelPopup.gameObject.activeInHierarchy;
        var season = CaptureSeason();
        string revision = Digest(JsonSerializer.Serialize(new { fish, hearts = data.breedCount, timestamp = data.breedTimestamp, level = data.tankLevel, exp = data.tankExp,
            season.id, season.active, season.starts_at, season.ends_at, season.error, season.recipes, species_catalog = speciesCatalog,
            retry_breeding = nativeBreedRetry.pending && nativeBreedRetry.account_context_hash == AccountContext() }));
        string blockReason = UiActivityGuard.BlockReason(networkBusy, confirm, levelPopupOpen, windowOpen, overlayOpen);
        bool busy = blockReason.Length != 0;
        var snapshot = new Snapshot { session = session, revision = revision, config_digest = configDigest, captured_at = Now, season = season,
            fish = fish, market_catalog = marketCatalog, species_catalog = speciesCatalog.ToArray(), hearts = data.breedCount, next_heart_at = GameClock.NextHeartAt(data.breedTimestamp, GameConfig.CF_BREED_COUNT_INTERVAL),
            capacity = GameConfig.CF_FISH_MAX_COUNT, ui_busy = busy, request_in_flight = inFlight, uncertain = uncertain,
            tank_level = data.tankLevel, tank_exp = data.tankExp, next_level_exp = nextExp, max_tank_level = maxLevel,
            max_tank_exp = GameConfig.CF_TANK_MAX_EXP, next_level_reward_fish_ids = rewardIds,
            level_up_available = canLevel, home_menu = home, network_busy = networkBusy, confirmation_open = confirm, level_up_popup_open = levelPopupOpen,
            ui_is_processing = ui.isProcessing, network_indicator_visible = Visible(ui.viewNetwork),
            breed_retry_after_server_restore = nativeBreedRetry.pending && nativeBreedRetry.account_context_hash == AccountContext(),
            window_open = windowOpen, overlay_open = overlayOpen, menu_name = ui.currentMenuType.ToString(), ui_block_reason = blockReason };
        season.plan = SeasonPolicy.Build(snapshot, cfg);
        return snapshot;
    }

    static SeasonState CaptureSeason()
    {
        var result = new SeasonState { id = CommonData.seasonId };
        try
        {
            if (!DateTimeOffset.TryParse(CommonData.seasonStartTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
                || !DateTimeOffset.TryParse(CommonData.seasonFinishTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
                throw new InvalidDataException("Game season dates missing");
            result.starts_at = start.ToUnixTimeSeconds(); result.ends_at = end.ToUnixTimeSeconds();
            result.active = GameUtil.IsSeasonActive();
            var loader = CSVLoader.Instance;
            if (loader.fishSeasonRecipeList == null || loader.fishTypeList == null) throw new InvalidDataException("Game recipe table missing");
            var recipes = new Dictionary<(string, int), List<SeasonIngredient>>();
            for (int i = 0; i < loader.fishSeasonRecipeList.Count; i++)
            {
                var row = loader.fishSeasonRecipeList[i];
                if (row.Season != result.id) continue;
                if (!loader.fishTypeList.ContainsKey(row.SeasonFishType) || loader.fishTypeList[row.SeasonFishType].Season != result.id
                    || !loader.fishTypeList.ContainsKey(row.RecipeFishType)) throw new InvalidDataException("Unknown season recipe fish");
                int materialSeason = loader.fishTypeList[row.RecipeFishType].Season;
                if (materialSeason != 0 && materialSeason != result.id) throw new InvalidDataException("Cross-season material unsupported");
                var key = (row.SeasonFishType, row.SeasonFishGrade);
                if (!recipes.ContainsKey(key)) recipes[key] = new();
                recipes[key].Add(new() { species = row.RecipeFishType, name = FishName(row.RecipeFishType), grade = row.RecipeFishGrade, amount = row.RecipeAmount, seasonal = materialSeason > 0 });
            }
            result.recipes = recipes.OrderBy(x => x.Key.Item1, StringComparer.Ordinal).ThenBy(x => x.Key.Item2)
                .Select(x => new SeasonRecipe { season = result.id, species = x.Key.Item1, name = FishName(x.Key.Item1), grade = x.Key.Item2, ingredients = x.Value.ToArray() }).ToArray();
        }
        catch (Exception) { result.error = "Could not read authoritative game season/recipe data"; }
        return result;
    }

    static string FishName(string species)
    {
        string term = "Fish/" + species.ToLowerInvariant();
        string name = I2.Loc.LocalizationManager.GetTranslation(term, true, 0, true, false, null, null, true);
        return string.IsNullOrWhiteSpace(name) || name == term ? species : name;
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
        RefreshGameViews(data, game);
        Atomic(Path.Combine(root, "snapshot.json"), Capture(data, ui, before.config_digest, cfg));
        message = $"Locked valuable fish: {keep.market_hash_name}";
        nextActionAt = Now + 1;
        Status("market_locked");
        return true;
    }

    void RefreshTankIndicators(GameDataManager data, GameManager game)
    {
        if (Now < nextDisplayRefresh) return;
        try { tankIndicators.Sync(data.breedCount, data.tankLevel, data.tankExp, () => game.CheckTankShowPopup()); displayNote = ""; }
        catch (Exception ex) { displayNote = ex.GetType().Name + ": " + ex.Message; nextDisplayRefresh = Now + 5; }
    }

    void RefreshGameViews(GameDataManager data, GameManager game)
    {
        RefreshTankIndicators(data, game);
        try { game.OnRefresh?.Invoke(); } catch (Exception ex) { displayNote = ex.GetType().Name + ": " + ex.Message; }
        try { game.OnReddotStatus?.Invoke(); } catch (Exception ex) { displayNote = ex.GetType().Name + ": " + ex.Message; }
    }

    bool TryAutoLevelUp(Snapshot before, Settings cfg, GameDataManager data, GameManager game, UIManager ui)
    {
        if (levelUpFeature.Paused || !LevelUpGuard.CanHandle(cfg, before)) return false;
        if (before.level_up_popup_open)
        {
            var popup = UnityEngine.Object.FindObjectOfType<UILevelUp>();
            if (popup == null) return false;
            return levelUpFeature.Run(() => popup.Ok());
        }
        if (Now < nextActionAt || !LevelUpGuard.CanUpgrade(cfg, before) || !data.IsCheckTankLevelUp()) return false;
        bool attempted = levelUpFeature.Run(() =>
        {
        var id = Guid.NewGuid().ToString("N");
        var journal = Path.Combine(root, "level-up-pending.json");
        var events = Path.Combine(root, "level-events");
        Directory.CreateDirectory(events);
        Atomic(journal, new { event_id = id, kind = "level_up", before, dispatched_at = Now });
        message = $"Leveling tank from {before.tank_level}";
        Status("leveling");
        // This normal game method checks XP/max level, expands the tank, grants
        // the level's fish/deco rewards and updates achievements exactly once.
        data.UpgradeTankLevel();
        var result = Capture(data, ui, before.config_digest, cfg);
        if (!LevelUpGuard.Confirm(before, result))
        {
            levelUpFeature.Pause("No level advance observed; ordinary automation continues without repeating the attempt");
            return;
        }
        data.SaveData();
        RefreshGameViews(data, game);
        var after = Capture(data, ui, before.config_digest, cfg);
        Atomic(Path.Combine(events, id + ".json"), new { event_id = id, kind = "level_up", confirmed = true, completed_at = Now,
            before_level = before.tank_level, after_level = after.tank_level, before_exp = before.tank_exp, after_exp = after.tank_exp,
            added_fish_ids = after.fish.Where(f => !before.fish.Any(old => old.id == f.id)).Select(f => f.id), exp_change = after.tank_exp - before.tank_exp,
            required_cumulative_exp = before.next_level_exp, before_fish_count = before.fish.Length, after_fish_count = after.fish.Length });
        File.Delete(journal);
        levelUpCount += after.tank_level - before.tank_level; nextActionAt = Now + 1;
        message = $"Confirmed tank level-up {before.tank_level} -> {after.tank_level}";
        Log.LogInfo(message);
        Atomic(Path.Combine(root, "snapshot.json"), Capture(data, ui, before.config_digest, cfg));
        Status("confirmed");
        });
        if (levelUpFeature.Paused) { message = "Automatic level-up deferred; ordinary automation continues"; Status("ready"); }
        return attempted;
    }

    void Dispatch(Command cmd, Snapshot before, GameDataManager data, GameManager game, Settings cfg)
    {
        if (cmd.kind == "breed")
        {
            DispatchOriginalBreed(cmd, before, data, game, UnityEngine.Object.FindObjectOfType<UIManager>(), cfg);
            return;
        }
        // Persist BEFORE calling the game's network method. A restart cannot repeat an ambiguous request.
        recovery = new PendingRecovery { cmd = cmd, before = before, dispatched_at = Now, account_context_hash = AccountContext() };
        absenceHistoryLoaded = false;
        Atomic(Path.Combine(root, "pending.json"), recovery);
        SaveRecovery();
        pendingId = cmd.request_id; pendingKind = cmd.kind; inFlight = true; dispatchedAt = Now;
        message = $"{cmd.kind}: {cmd.reason}";
        Log.LogInfo($"Dispatch {cmd.kind} ({cmd.fish_ids.Length} fish), request {cmd.request_id}");
        Status("requesting");
        callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NetworkResult, bool, string>>(
            new Action<NetworkResult, bool, string>((result, isNew, childId) =>
            {
                try
                {
                    if (!inFlight || pendingId != cmd.request_id) return;
                    if (result != NetworkResult.Success)
                    {
                        var rejected = Capture(data, UnityEngine.Object.FindObjectOfType<UIManager>(), before.config_digest, cfg);
                        if (RequestOutcomeGuard.CanConfirmRejection(result == NetworkResult.Fail, childId, before, rejected))
                        {
                            var rejectedEvents = Path.Combine(root, "rejected-events");
                            Directory.CreateDirectory(rejectedEvents);
                            Atomic(Path.Combine(rejectedEvents, cmd.request_id + ".json"), new { event_id = cmd.request_id,
                                kind = cmd.kind, outcome = "rejected", consumed = false, completed_at = Now, cmd, before, after = rejected });
                            File.Delete(Path.Combine(root, "pending.json"));
                            SettleResponseBindings(cmd.request_id);
                            ReleaseRequestUi(cmd.request_id, UnityEngine.Object.FindObjectOfType<UIManager>());
                            recovery = null;
                            inFlight = false; uncertain = false; pendingId = "";
                            nextActionAt = Now + cfg.minimum_action_interval_seconds;
                            message = $"{cmd.kind} rejected without consumption; replanning another combination";
                            Log.LogInfo(message); Status("waiting"); return;
                        }
                        uncertain = true; inFlight = false; message = $"{result}; outcome requires reconciliation";
                        Status("uncertain"); return;
                    }
                    // Only merge/craft reach this callback. Breeding uses the
                    // original UIBreed handler, including its own saved cost.
                    data.SaveData();
                    if (recovery != null) { recovery.accounting_saved = true; SaveRecovery(); }
                    var ui = UnityEngine.Object.FindObjectOfType<UIManager>();
                    ReleaseRequestUi(cmd.request_id, ui);
                    // Normal UIBreed success calls CheckTankShowPopup after
                    // spending/saving. OnRefresh alone does not redraw the
                    // aquarium's UIBreedTankPopup heart sprites.
                    RefreshGameViews(data, game);
                    var after = Capture(data, ui, before.config_digest, cfg);
                    var child = after.fish.SingleOrDefault(f => f.id == childId);
                    if (child == null || before.fish.Any(f => f.id == childId)) throw new InvalidDataException("Success child was not a new inventory item");
                    var parents = cmd.fish_ids.Select(id => before.fish.Single(f => f.id == id)).ToArray();
                    if (cmd.kind == "merge" && cmd.fish_ids.Any(id => after.fish.Any(f => f.id == id))) throw new InvalidDataException("Merge material still present after success");
                    if (cmd.kind == "craft") SeasonPolicy.Confirm(cmd, before, after, child);
                    var record = new { event_id = cmd.request_id, kind = cmd.kind, confirmed = true, synthetic = false, completed_at = Now,
                        child_id = childId, session,
                        server_confirmed_at = receipt?.ConfirmedAt ?? 0,
                        fish_ids = cmd.fish_ids, before_fish_count = before.fish.Length, after_fish_count = after.fish.Length,
                        season_reserved_ids = before.season.plan.reserved_ids, merge_price_basis = cfg.market_merge_price_basis,
                        material_prices = parents.Select(f => new { f.id, f.market_hash_name, f.market_value_source,
                            f.market_best_seller_proceeds_krw, f.market_estimated_seller_proceeds_krw, f.market_estimate_peer_count }),
                        parents = parents.Select(f => f.Traits()), result = child.Traits(Math.Max(0, child.ready_at - Now)), before_hearts = before.hearts, after_hearts = data.breedCount };
                    // One immutable event per request; the learner deduplicates IDs too.
                    if (cmd.kind == "craft")
                        Atomic(Path.Combine(root, "craft-events", cmd.request_id + ".json"), new { event_id = cmd.request_id, kind = "craft", confirmed = true,
                            completed_at = Now, cmd.season, cmd.target_species, cmd.target_grade, material_ids = cmd.fish_ids,
                            materials = parents.Select(f => f.Traits()), result = child.Traits(), before_hearts = before.hearts, after_hearts = after.hearts });
                    else Atomic(Path.Combine(root, "events", cmd.request_id + ".json"), record);
                    File.Delete(Path.Combine(root, "pending.json"));
                    SettleResponseBindings(cmd.request_id);
                    recovery = null;
                    inFlight = false; uncertain = false; pendingId = ""; nextActionAt = Now + cfg.minimum_action_interval_seconds;
                    message = $"Confirmed {cmd.kind}; result Level {child.level}, Grade {child.grade}";
                    Log.LogInfo(message);
                    Status("confirmed");
                }
                catch (Exception ex)
                {
                    if (recovery != null) { recovery.validation_failed = true; SaveRecovery(); }
                    uncertain = true; inFlight = false; message = ex.GetType().Name + ": " + ex.Message; Log.LogError(message); Status("uncertain");
                }
            }));
        Directory.CreateDirectory(Path.Combine(root, "events"));
        Directory.CreateDirectory(Path.Combine(root, "craft-events"));
        var network = UnityEngine.Object.FindObjectOfType<NetworkManager>();
        if (network == null) throw new InvalidDataException("Network manager unavailable");
        var ids = new Il2CppStringArray(cmd.fish_ids);
        var requestUi = UnityEngine.Object.FindObjectOfType<UIManager>();
        requestUiLease.Begin(cmd.request_id, requestUi.isProcessing, Visible(requestUi.viewNetwork));
        dispatchedCommand = cmd;
        dispatchBefore = before;
        bindingNativeRequest = true;
        try
        {
            if (cmd.kind == "craft") network.FishCraft(cmd.target_species, cmd.target_grade, ids, callback);
            else network.FishMerge(ids, callback);
        }
        finally { bindingNativeRequest = false; }
    }

    static void BindRequest(NANOOGame.Network.NetworkManager __instance, string __0, string __1, Il2CppSystem.Action<string> __2)
    {
        var p = Current;
        if (p == null || !p.bindingNativeRequest || !p.inFlight || p.dispatchedCommand == null
            || __0 != "https://gapi.nanoocompany.com/cs/pcfish/" + p.pendingKind + "/run") return;
        // Binding is part of the transaction contract, not optional logging.
        // Keep the native callback target rooted until its receipt is settled.
        var target = __2.Target ?? throw new InvalidDataException("Native response callback has no target");
        var lifetime = new NativeResponseLifetime(target.Pointer);
        p.responseBindings.Register(target.Pointer.ToInt64(), p.dispatchedCommand.request_id,
            new NativeResponseBinding(p.dispatchedCommand, p.dispatchBefore, AccountContext(), target), lifetime.Alive, lifetime.Release);
        try
        {
            var diagnostic = RequestWireContract.Inspect(p.dispatchedCommand, __1);
            var folder = Path.Combine(p.root, "network-requests");
            Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, p.pendingId + ".json"), new { request_id = p.pendingId,
                kind = p.pendingKind, captured_at = Now, diagnostic, auth_context_hash = Digest(__instance.AccessToken ?? "") });
            if (p.pendingKind == "breed" && p.nativeBreedRetry.Dispatch(p.pendingId, AccountContext()))
            {
                p.SaveNativeBreedRetry();
                p.NativeComparisonStage("new_breed_after_server_restore_dispatched", new {
                    restored_request_id = p.nativeBreedRetry.restored_request_id, new_request_id = p.pendingId });
            }
        }
        catch (Exception ex) { p.responseDiagnosticNote = "Request diagnostic deferred: " + ex.GetType().Name; }
        // Observation only: the native game remains responsible for the body.
    }

    static bool ObserveResponse(object __instance, string __0)
    {
        var plugin = Current;
        if (plugin == null || plugin.replayingReceipt) return true;
        if (__instance is not Il2CppObjectBase native || !plugin.responseBindings.TryGet(native.Pointer.ToInt64(), out var entry)) return true;
        if (entry.Settled) { plugin.ObserveSettledResponse(entry.RequestId, __0); return false; }
        var binding = entry.Payload;
        try
        {
            var diagnostic = ResponseDiagnostics.Read(__0);
            try
            {
                var folder = Path.Combine(plugin.root, "network-responses"); Directory.CreateDirectory(folder);
                var callbacks = Path.Combine(plugin.root, "response-callback-events"); Directory.CreateDirectory(callbacks);
                Atomic(Path.Combine(callbacks, Guid.NewGuid().ToString("N") + ".json"), new { request_id = binding.command.request_id,
                    captured_at = Now, diagnostic.status, diagnostic.child_uid,
                    current_request = plugin.pendingId == binding.command.request_id,
                    duplicate_child_receipt = plugin.observedChildReceipts.Contains(diagnostic.child_uid) });
                Atomic(Path.Combine(folder, binding.command.request_id + ".json"), new { request_id = binding.command.request_id,
                    kind = binding.command.kind, captured_at = Now, diagnostic });
            }
            catch (Exception ex) { plugin.responseDiagnosticNote = "Response diagnostic deferred: " + ex.GetType().Name; }
            if (diagnostic.status == "fail" && diagnostic.errors.TryGetValue("message", out var error)
                && error.StartsWith("Missing fishIds", StringComparison.OrdinalIgnoreCase))
            {
                plugin.inventorySynced = false;
                plugin.inventorySyncNote = "Server could not find local fish IDs; reconciliation required";
            }
            if (!plugin.inFlight || plugin.pendingId != binding.command.request_id)
            {
                if (diagnostic.status == "success" && !plugin.observedChildReceipts.Contains(diagnostic.child_uid))
                {
                    plugin.inventorySynced = false;
                    plugin.inventorySyncNote = "Late response retained for authoritative reconciliation";
                }
                return false;
            }
            if (diagnostic.status != "success")
            {
                if (plugin.recovery != null) { plugin.recovery.response_status = diagnostic.status; plugin.SaveRecovery(); }
                return true;
            }
            if (plugin.observedChildReceipts.Contains(diagnostic.child_uid)) return false;
            if (plugin.NativeBreedRequest)
            {
                // Retain the existing new-UID contract without adding a GET or
                // withholding a valid original success callback.
                _ = new ReceiptCommit(binding.command, binding.before, diagnostic.child_uid, binding.account);
                plugin.observedChildReceipts.Add(diagnostic.child_uid);
                plugin.recovery.child_id = diagnostic.child_uid;
                plugin.recovery.response_status = "success";
                plugin.recovery.local_commit_started = true;
                plugin.SaveRecovery();
                plugin.NativeComparisonStage("original_success_callback_entered", new { child_id = diagnostic.child_uid,
                    success_callback_held = false });
                return true;
            }
            if (plugin.receipt != null && plugin.receipt.ChildId != diagnostic.child_uid)
            {
                if (plugin.recovery != null) { plugin.recovery.validation_failed = true; plugin.SaveRecovery(); }
                plugin.uncertain = true; plugin.message = "Conflicting child UIDs for one request; receipts retained";
                plugin.Status("uncertain"); return false;
            }
            plugin.observedChildReceipts.Add(diagnostic.child_uid);
            plugin.receipt = new ReceiptCommit(binding.command, binding.before, diagnostic.child_uid, binding.account);
            if (plugin.recovery != null)
            { plugin.recovery.child_id = diagnostic.child_uid; plugin.recovery.response_status = "success"; plugin.SaveRecovery(); }
            var method = __instance.GetType().GetMethod(binding.command.kind == "breed" ? "_FishBreed_b__0" : binding.command.kind == "merge" ? "_FishMerge_b__0" : "_FishCraft_b__0");
            plugin.receiptContinuation = () => method.Invoke(__instance, new object[] { __0 });
            plugin.nextReceiptCheck = Now;
            plugin.message = "Server response received; verifying ownership before local commit";
            plugin.Status("waiting");
            return false;
        }
        catch (Exception ex)
        {
            if (plugin.NativeBreedRequest)
            { plugin.recovery.validation_failed = true; plugin.recovery.note = "Invalid original breeding receipt: " + ex.GetType().Name; plugin.SaveRecovery(); }
            plugin.uncertain = true; plugin.inFlight = false;
            plugin.message = "Receipt verification could not start: " + ex.GetType().Name;
            plugin.Status("uncertain"); return false;
        }
    }

    static bool ObserveInventoryResponse(string __0)
    {
        var p = Current;
        if (p == null || !p.inventorySyncing) return true;
        if (p.syncingAccountContext != AccountContext())
        {
            p.inventorySyncing = false; p.nextInventorySync = Now + 2;
            p.inventorySyncNote = "Account changed during inventory query; stale response was not applied";
            var ui = UnityEngine.Object.FindObjectOfType<UIManager>();
            if (ui != null) { ui.isProcessing = false; if (ui.viewNetwork != null) ui.viewNetwork.SetActive(false); }
            return false;
        }
        try { p.serverInventoryIds = InventorySyncEvidence.ReadIds(__0); }
        catch (Exception ex) { p.serverInventoryIds = null; p.inventorySyncNote = "Inventory evidence unavailable: " + ex.GetType().Name; }
        return true;
    }

    void StartInventorySync(Snapshot before, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        ReserveInventoryRead("restore", Guid.NewGuid().ToString());
        inventorySyncing = true; serverInventoryIds = null;
        syncingAccountContext = AccountContext();
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(root, "inventory-sync-events");
        Directory.CreateDirectory(folder);
        var journal = Path.Combine(folder, id + ".pending.json");
        Atomic(journal, new { event_id = id, started_at = Now, before });
        inventorySyncCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(new Action<bool>(ok =>
        {
            try
            {
                if (!ok || serverInventoryIds == null) throw new InvalidDataException("Server inventory query did not provide authoritative IDs");
                var after = Capture(data, ui, before.config_digest, cfg);
                InventorySyncEvidence.Confirm(before, after, serverInventoryIds);
                Atomic(Path.Combine(folder, id + ".json"), new { event_id = id, completed_at = Now, outcome = "reconciled", server_fish_ids = serverInventoryIds,
                    removed_local_ids = before.fish.Where(f => !after.fish.Any(a => a.id == f.id)).Select(f => f.id),
                    added_local_ids = after.fish.Where(f => !before.fish.Any(a => a.id == f.id)).Select(f => f.id), before, after });
                File.Delete(journal);
                inventorySynced = true; inventoryAccountContext = syncingAccountContext;
                serverInventoryVerifiedAt = Now; inventoryPacing.Healthy(); SavePacing();
                inventoryPacing.last_completed_at = Now; SavePacing();
                inventorySyncNote = "Reconciled against server inventory";
                RefreshGameViews(data, game);
                message = $"Server inventory reconciled: {before.fish.Length} -> {after.fish.Length} local fish";
                Log.LogInfo(message);
            }
            catch (Exception ex) { DeferInventoryRead(); inventorySyncNote = ex.GetType().Name + ": " + ex.Message; message = "Inventory reconciliation deferred"; }
            finally { inventorySyncing = false; nextInventorySync = Now + 60; Status(inventorySynced ? "ready" : "waiting"); }
        }));
        message = "Reconciling inventory using the game's server query"; Status("waiting");
        // Native ManageRestore performs a GET, updates server-owned fish counts
        // and cooldowns, adds missing server items and drops stale local entries.
        // It does not breed, merge, trade or consume a heart.
        try { UnityEngine.Object.FindObjectOfType<NetworkManager>().ManageRestore(inventorySyncCallback); }
        catch (Exception ex)
        {
            inventorySyncing = false; nextInventorySync = Now + 60;
            inventorySyncNote = "Inventory query deferred: " + ex.GetType().Name;
            message = "Waiting to query server inventory"; Status("waiting");
        }
    }

    static bool InterceptProbeGet(NANOOGame.Network.NetworkManager __instance, string __0, Il2CppSystem.Action<string> __1)
    {
        var p = Current;
        if (p == null || !p.probeBinding || __0 != "https://gapi.nanoocompany.com/cs/pcfish/manage/search") return true;
        p.probeBinding = false;
        p.probeContext = Digest(__instance.AccessToken ?? "");
        p.probeNativeRestore = __1;
        // The native wrapper has configured its SDK context. Replace only our
        // diagnostic invocation's callbacks so no restore/removal code runs.
        __instance.RequestGet(__0, p.probeResponse, p.probeError);
        return false;
    }

    void StartReadOnlyProbe(string id, Snapshot before, UIManager ui, Action<string[]> completed = null)
    {
        ReserveInventoryRead("read_only", id);
        var ticket = new InventoryProbeTicket(id, AccountContext(), Now);
        activeProbe = ticket;
        probeId = id; probeBefore = before; probeInFlight = true;
        probeCompleted = completed; probeWasProcessing = ui.isProcessing;
        probeWasVisible = ui.viewNetwork != null && ui.viewNetwork.activeSelf;
        probeResponse = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(new Action<string>(response => FinishProbe(ticket, response, ui)));
        probeError = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => FinishProbe(ticket, "", ui)));
        ignoredProbeCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(new Action<bool>(_ => { }));
        probeBinding = true;
        try { UnityEngine.Object.FindObjectOfType<NetworkManager>().ManageRestore(ignoredProbeCallback); }
        catch (Exception) { FinishProbe(ticket, "", ui); }
        finally { probeBinding = false; }
    }

    void FinishProbe(InventoryProbeTicket ticket, string response, UIManager ui)
    {
        // A timeout/duplicate from an older GET must never complete a newer
        // request or authorize its consumptive continuation.
        if (!ticket.TryComplete(activeProbe)) return;
        var nativeRestore = probeNativeRestore;
        string[] confirmedIds = null;
        try
        {
            if (!ticket.SameAccount(AccountContext())) throw new InvalidDataException("Account changed during inventory query");
            var ids = InventorySyncEvidence.ReadIds(response);
            confirmedIds = ids;
            var data = UnityEngine.Object.FindObjectOfType<GameDataManager>();
            var local = Enumerable.Range(0, data.fishList.Count).Select(i => data.fishList[i].Id).ToArray();
            var folder = Path.Combine(root, "inventory-probes"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, probeId + ".json"), new { request_id = probeId, captured_at = Now, session,
                auth_context_hash = probeContext, account_context_hash = ticket.AccountContext, server_ids = ids, local_before = probeBefore.fish.Select(f => f.id), local_after = local,
                local_only = probeBefore.fish.Where(f => !f.basic && !ids.Contains(f.id)).Select(f => new { f.id, f.species, f.ready_at, f.ready }),
                server_only = ids.Where(id => !local.Contains(id)) });
        }
        catch (Exception ex) { responseDiagnosticNote = "Read-only probe failed: " + ex.GetType().Name; }
        finally
        {
            probeInFlight = false;
            activeProbe = null;
            ui.isProcessing = probeWasProcessing;
            if (ui.viewNetwork != null) ui.viewNetwork.SetActive(probeWasVisible);
            message = "Read-only server inventory probe completed"; Status("ready");
        }
        var completed = probeCompleted; probeCompleted = null;
        if (confirmedIds == null) DeferInventoryRead();
        else { inventoryPacing.last_completed_at = Now; SavePacing(); }
        // This continuation exists only during this exact, authenticated GET's
        // completion. Restart recovery may apply its already verified result.
        applyVerifiedProbeRestore = confirmedIds == null ? null : () => nativeRestore.Invoke(response);
        try
        {
            completed?.Invoke(confirmedIds);
            if (confirmedIds != null && !NativeComparisonActive) ObserveDeferredReceipts(confirmedIds, ui);
        }
        finally { applyVerifiedProbeRestore = null; }
    }

    static string AccountContext() => Digest((CommonData.steamId ?? "") + "|" + (CommonData.userId ?? ""));

    void ConfirmReceipt(string[] ids)
    {
        if (ids == null || receipt == null) return;
        try
        {
            if (!receipt.Confirm(ids, AccountContext(), Now))
            {
                var data = UnityEngine.Object.FindObjectOfType<GameDataManager>();
                var ui = UnityEngine.Object.FindObjectOfType<UIManager>();
                var raw = ReadText(configFile); var cfg = JsonSerializer.Deserialize<Settings>(raw);
                if (TryDetachUnappliedBasic(ids, Capture(data, ui, Digest(raw), cfg), data, UnityEngine.Object.FindObjectOfType<GameManager>(), cfg)) return;
                DeferInventoryRead(); return;
            }
            var folder = Path.Combine(root, "receipt-confirmations"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, pendingId + ".json"), new { request_id = pendingId, confirmed_at = receipt.ConfirmedAt,
                child_id = receipt.ChildId, account_context_hash = receipt.AccountContext, auth_context_hash = probeContext,
                server_ids = ids, local_commit_started = false });
            receipt.ApplyOnce(() =>
            {
                if (recovery != null) { recovery.local_commit_started = true; SaveRecovery(); }
                replayingReceipt = true;
                try { receiptContinuation(); }
                finally { replayingReceipt = false; }
            });
            serverInventoryIds = ids;
            inventoryAccountContext = receipt.AccountContext;
            serverInventoryVerifiedAt = Now; inventoryPacing.Healthy(); SavePacing();
            receipt = null; receiptContinuation = null;
        }
        catch (Exception ex) { uncertain = true; inFlight = false; message = "Verified receipt commit failed: " + ex.GetType().Name; Status("uncertain"); }
    }

    static void ObserveSave(GameDataManager __instance) => ObservePersistence(__instance, "save");
    static void ObserveLoad(GameDataManager __instance) => ObservePersistence(__instance, "load");
    static void ObservePersistence(GameDataManager data, string operation)
    {
        var p = Current;
        if (p == null || data.fishList == null) return;
        try
        {
            if (operation == "save") p.NativeComparisonSaveObserved(data);
            var fish = Enumerable.Range(0, data.fishList.Count).Select(i => data.fishList[i])
                .Select(f => new { id = f.Id, species = f.Type, breeds = f.BreedCount, f.BreedNextDatetime }).OrderBy(f => f.id).ToArray();
            var signature = Digest(JsonSerializer.Serialize(fish));
            if (operation == "save" && signature == p.lastSavedInventory) return;
            p.lastSavedInventory = signature;
            var path = Path.Combine(Application.persistentDataPath, "SaveFile.es3");
            string fileHash = "";
            if (File.Exists(path)) using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var hash = SHA256.Create()) fileHash = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
            var folder = Path.Combine(p.root, "persistence-events"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), new { operation, captured_at = Now, p.session,
                request_id = p.pendingId, p.pendingKind, p.inventorySyncing, inventory_signature = signature, save_sha256 = fileHash, fish });
        }
        catch (Exception ex) { p.responseDiagnosticNote = "Persistence observation deferred: " + ex.GetType().Name; }
    }
}

public sealed class Driver(IntPtr pointer) : MonoBehaviour(pointer)
{
    public void Update() => Plugin.Current.Tick();
}
