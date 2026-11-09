using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NN.PF.Core.Managers;
using NN.PF.Core.Network;
using NN.PF.UI.Breed;

namespace PCFish.Automation;

public sealed partial class Plugin
{
    NativeComparisonPlan nativeComparison;
    UIBreed nativeComparisonUi;
    string NativeComparisonPath => Path.Combine(root, "native-comparison.json");
    bool NativeComparisonActive => nativeComparison is { enabled: true };
    bool NativeComparisonRequest => recovery?.flow == "native_comparison";
    bool NativeBreedRequest => recovery?.flow is "native_comparison" or "native_breed";
    void SaveNativeComparison() => Atomic(NativeComparisonPath, nativeComparison);

    void InstallNativeComparison(Harmony harmony)
    {
        var method = typeof(UIBreed).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name.Contains("Breed") && m.Name.Contains("24_2"));
        harmony.Patch(method,
            prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(BeforeNativeUiResult), BindingFlags.Static | BindingFlags.NonPublic)),
            postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(AfterNativeUiResult), BindingFlags.Static | BindingFlags.NonPublic)));
    }

    void NativeComparisonStage(string stage, object detail = null)
    {
        var folder = Path.Combine(root, NativeComparisonRequest ? "native-comparison-events" : "native-breed-events"); Directory.CreateDirectory(folder);
        Atomic(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), new {
            comparison_id = NativeComparisonRequest ? nativeComparison?.comparison_id : null, request_id = pendingId, stage, captured_at = Now, session, detail });
    }

    bool TryNativeComparison(Snapshot snapshot, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        if (nativeComparison == null && File.Exists(NativeComparisonPath))
        {
            nativeComparison = JsonSerializer.Deserialize<NativeComparisonPlan>(ReadText(NativeComparisonPath));
            nativeComparison.Validate();
            if (nativeComparison.enabled && nativeComparison.active_request_id.Length == 0)
            {
                // The normal game's startup/login traffic is outside our hooks.
                // Establish a quiet interval before the first comparison PUT.
                nativeComparison.not_before = Math.Max(nativeComparison.not_before, Now + nativeComparison.quiet_seconds);
                SaveNativeComparison();
            }
        }
        if (!NativeComparisonActive && !NativeComparisonRequest) return false;
        if (File.Exists(Path.Combine(root, "consumption-alert.json")))
        { uncertain = true; message = "Consumption alert retained; native comparison is paused"; Status("uncertain"); return true; }
        if (nativeComparison == null || nativeComparison.account_context_hash != AccountContext())
        { message = "Native comparison retained: authenticated account differs"; Status("waiting"); return true; }
        if (!cfg.enabled || cfg.mode == "observe")
        { message = "Native comparison paused by configuration"; Status("paused"); return true; }
        if (nativeComparison.active_request_id.Length != 0)
        {
            if (recovery == null || recovery.cmd.request_id != nativeComparison.active_request_id || !NativeComparisonRequest)
            { uncertain = true; message = "Native comparison journal requires reconciliation; no request resent"; Status("uncertain"); return true; }
            if (recovery.validation_failed)
            { message = recovery.note; Status("uncertain"); return true; }
            if (!nativeComparison.CanRead(recovery, AccountContext(), Now))
            {
                if (recovery.native_completed_at == 0 && Now - dispatchedAt > 60)
                { uncertain = true; message = "Original native callback did not complete; journal retained"; Status("uncertain"); }
                else { message = "Native game result saved; waiting through comparison quiet interval"; Status("waiting_network"); }
                return true;
            }
            if (!probeInFlight && inventoryPacing.Ready(Now))
                StartReadOnlyProbe(Guid.NewGuid().ToString(), snapshot, ui, ids => FinishOriginalBreed(ids, data, game, ui, cfg));
            return true;
        }
        if (nativeComparison.completed_trials >= nativeComparison.maximum_trials || Now >= nativeComparison.expires_at)
        {
            nativeComparison.enabled = false;
            nativeComparison.state = nativeComparison.completed_trials >= nativeComparison.maximum_trials ? "completed" : "expired";
            SaveNativeComparison();
            message = "Bounded native-order comparison finished"; Status("ready"); return true;
        }
        if (!nativeComparison.CanStart(snapshot, cfg, AccountContext(), Now) || Now < nextActionAt)
        { message = "Native comparison waiting for heart, mature starter parents or quiet interval"; Status("waiting"); return true; }
        var parents = snapshot.fish.Where(f => f.basic && f.ready && !f.locked).OrderBy(f => f.id).Take(2).ToArray();
        if (parents.Length != 2) { message = "Native comparison waiting for two mature starter fish"; Status("waiting"); return true; }
        var cmd = new Command { request_id = Guid.NewGuid().ToString(), kind = "breed", session = session,
            revision = snapshot.revision, config_digest = snapshot.config_digest, expires_at = Now + 10,
            fish_ids = parents.Select(f => f.id).ToArray(), reason = "Bounded original UIBreed.Breed comparison; no pre/post inventory query" };
        ExecutionGuard.Validate(cmd, snapshot, cfg);
        DispatchOriginalBreed(cmd, snapshot, data, game, ui, cfg, comparison: true);
        return true;
    }

    void DispatchOriginalBreed(Command cmd, Snapshot snapshot, GameDataManager data, GameManager game, UIManager ui, Settings cfg, bool comparison = false)
    {
        nativeComparisonUi = UnityEngine.Object.FindObjectOfType<UIBreed>(true)
            ?? throw new InvalidDataException("Original UIBreed component is unavailable");
        recovery = new PendingRecovery { cmd = cmd, before = snapshot, dispatched_at = Now,
            account_context_hash = AccountContext(), flow = comparison ? "native_comparison" : "native_breed" };
        Atomic(Path.Combine(root, "pending.json"), recovery); SaveRecovery();
        if (comparison)
        { nativeComparison.active_request_id = cmd.request_id; nativeComparison.state = "requesting"; SaveNativeComparison(); }
        pendingId = cmd.request_id; pendingKind = "breed"; dispatchedAt = Now; inFlight = true; uncertain = false;
        requestUiLease.Begin(cmd.request_id, ui.isProcessing, Visible(ui.viewNetwork));
        dispatchedCommand = cmd; dispatchBefore = snapshot;
        nativeComparisonUi.parentFishList = new Il2CppStringArray(cmd.fish_ids);
        NativeComparisonStage("before_original_ui_breed", new { hearts = snapshot.hearts, fish_ids = cmd.fish_ids,
            server_input_ownership_check = !comparison && cmd.fish_ids.Any(id => !snapshot.fish.Single(f => f.id == id).basic),
            success_callback_held = false, quiet_seconds = comparison ? nativeComparison.quiet_seconds : 60 });
        bindingNativeRequest = true;
        try { nativeComparisonUi.Breed(); }
        finally { bindingNativeRequest = false; }
        message = "Original UIBreed.Breed invoked; original success handlers remain immediate"; Status("requesting");
    }

    static bool BeforeNativeUiResult(NetworkResult __0, string __2)
    {
        var p = Current;
        if (p == null || !p.NativeBreedRequest || __0 != NetworkResult.Success || p.recovery.child_id != __2) return true;
        if (p.recovery.accounting_saved) return false;
        var data = UnityEngine.Object.FindObjectOfType<GameDataManager>();
        p.recovery.native_ui_hearts_before = data.breedCount;
        p.BeginAccounting();
        p.NativeComparisonStage("before_original_ui_accounting", new { child_id = __2, hearts = data.breedCount,
            child_already_local = Enumerable.Range(0, data.fishList.Count).Any(i => data.fishList[i].Id == __2) });
        return true;
    }

    void NativeComparisonSaveObserved(GameDataManager data)
    {
        if (!NativeBreedRequest || !recovery.accounting_started || recovery.accounting_saved
            || recovery.native_ui_hearts_before < 1 || data.breedCount != recovery.native_ui_hearts_before - 1) return;
        if (!Enumerable.Range(0, data.fishList.Count).Any(i => data.fishList[i].Id == recovery.child_id)) return;
        recovery.accounting_saved = true; recovery.native_completed_at = Now; recovery.native_hearts_after = data.breedCount;
        SaveRecovery();
        NativeComparisonStage("original_heart_save_completed", new { child_id = recovery.child_id, hearts = data.breedCount,
            fish_ids = Enumerable.Range(0, data.fishList.Count).Select(i => data.fishList[i].Id).ToArray() });
    }

    static void AfterNativeUiResult(NetworkResult __0, string __2)
    {
        var p = Current;
        if (p == null || !p.NativeBreedRequest || p.recovery.child_id != __2) return;
        if (__0 != NetworkResult.Success)
        { p.recovery.native_completed_at = Now; p.SaveRecovery(); }
        p.NativeComparisonStage("original_ui_callback_returned", new { result = __0.ToString(), child_id = __2,
            p.recovery.accounting_saved, p.recovery.native_completed_at });
    }

    bool TryNativeBreedPending(Snapshot snapshot, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        if (!NativeBreedRequest || NativeComparisonRequest) return false;
        if (File.Exists(Path.Combine(root, "consumption-alert.json")))
        { uncertain = true; message = "Consumption alert retained; original breeding result requires reconciliation"; Status("uncertain"); return true; }
        if (!cfg.enabled || cfg.mode == "observe")
        { message = "Original native breeding result retained while paused"; Status("paused"); return true; }
        if (recovery.validation_failed && !NativeServerRestore.CanRecheck(recovery))
        { message = recovery.note; Status("uncertain"); return true; }
        if (recovery.validation_failed && snapshot.ui_busy)
        { message = "Waiting for the visible game interaction before native server-data recovery"; Status("waiting"); return true; }
        if (recovery.native_completed_at == 0)
        {
            if (Now - dispatchedAt > 60)
            {
                // Do not invoke the original callback a second time. Existing
                // reconnect recovery may inspect server ownership/checkpoints.
                inFlight = false;
                if (TryRecoverPending(snapshot, data, game, ui, cfg)) return true;
            }
            message = "Waiting for original game breeding callback"; Status("requesting"); return true;
        }
        if (Now < recovery.native_completed_at + 60 || !inventoryPacing.Ready(Now))
        { message = "Original game result saved; waiting before delayed inventory verification"; Status("waiting_network"); return true; }
        // The retained 0.4.20 missing-UID stop is a resumable verification
        // checkpoint. Other validation failures returned above remain stopped.
        if (recovery.validation_failed) { recovery.validation_failed = false; SaveRecovery(); }
        StartReadOnlyProbe(Guid.NewGuid().ToString(), snapshot, ui, ids => FinishOriginalBreed(ids, data, game, ui, cfg));
        return true;
    }

    void FinishOriginalBreed(string[] ids, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        try { FinishOriginalBreedCore(ids, data, game, ui, cfg); }
        catch (Exception ex)
        {
            if (recovery != null)
            { recovery.validation_failed = true; recovery.note = "Original native result retained: " + ex.GetType().Name + ": " + ex.Message; SaveRecovery(); }
            uncertain = true; inFlight = false; message = recovery?.note ?? "Original native result retained"; Status("uncertain");
        }
    }

    void FinishOriginalBreedCore(string[] ids, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        if (ids == null) return;
        var work = recovery;
        if (work == null || !NativeBreedRequest || work.account_context_hash != AccountContext()
            || NativeComparisonRequest && !nativeComparison.CanRead(work, AccountContext(), Now)
            || !NativeComparisonRequest && Now < work.native_completed_at + 60) return;
        bool comparison = NativeComparisonRequest;
        var current = Capture(data, ui, Digest(ReadText(configFile)), cfg);
        if (work.response_status != "success")
        {
            if (work.response_status == "fail" && RequestOutcomeGuard.CanConfirmRejection(true, work.child_id, work.before, current)
                && current.fish.Where(f => !f.basic).Select(f => f.id).ToHashSet(StringComparer.Ordinal).SetEquals(ids))
            {
                var rejected = Path.Combine(root, "rejected-events"); Directory.CreateDirectory(rejected);
                Atomic(Path.Combine(rejected, work.cmd.request_id + ".json"), new { event_id = work.cmd.request_id,
                    kind = "breed", outcome = "rejected", consumed = false, flow = work.flow, completed_at = Now, cmd = work.cmd, before = work.before, after = current });
                if (comparison)
                {
                    nativeComparison.active_request_id = ""; nativeComparison.enabled = false;
                    nativeComparison.state = "native_rejection_without_consumption"; SaveNativeComparison();
                }
                FinishRecoveredRequest("rejected_without_consumption", ids, current, data, game, cfg);
            }
            else { work.note = "Original native network outcome is still unproven; mutation will not be resent"; SaveRecovery(); DeferInventoryRead(); }
            return;
        }
        var proof = work.Receipt();
        bool local = current.fish.Any(f => f.id == work.child_id);
        bool confirmed = proof.Confirm(ids, AccountContext(), Now);
        NativeComparisonStage("delayed_server_inventory", new { child_id = work.child_id, local_present = local,
            server_present = ids.Contains(work.child_id), confirmed, elapsed_since_native_save = Now - work.native_completed_at,
            work.accounting_saved, server_ids = ids });
        if (!confirmed)
        {
            work.absent_confirmations++; work.note = proof.Note; SaveRecovery();
            if (!comparison && RestoreNativeBreedInventory(ids, current, data, game, ui, cfg)) return;
            if (work.absent_confirmations >= 3)
            {
                work.validation_failed = true; work.note = "Original native breeding could not be verified after repeated delayed queries: " + proof.Note;
                SaveRecovery(); uncertain = true; inFlight = false;
                if (comparison) { nativeComparison.state = "native_server_mismatch"; nativeComparison.note = work.note; SaveNativeComparison(); }
                message = work.note; Status("uncertain");
            }
            else { inventoryPacing.next_at = Math.Max(inventoryPacing.next_at, Now + (comparison ? nativeComparison.quiet_seconds : 60)); SavePacing(); }
            return;
        }
        NativeBreedEvidence.ConfirmLocal(work, current);
        if (!comparison && nativeBreedRetry.consecutive_restores > 0)
        { nativeBreedRetry.Confirmed(AccountContext()); SaveNativeBreedRetry(); }
        var child = current.fish.Single(f => f.id == work.child_id);
        var folder = Path.Combine(root, "events"); Directory.CreateDirectory(folder);
        Atomic(Path.Combine(folder, work.cmd.request_id + ".json"), new {
            event_id = work.cmd.request_id, kind = "breed", confirmed = true, synthetic = false, flow = work.flow, session,
            completed_at = work.native_completed_at, server_confirmed_at = Now, child_id = work.child_id,
            fish_ids = work.cmd.fish_ids, before_fish_count = work.before.fish.Length, after_fish_count = current.fish.Length,
            parents = work.cmd.fish_ids.Select(id => work.before.fish.Single(f => f.id == id).Traits()),
            result = child.Traits(Math.Max(0, child.ready_at - work.native_completed_at)),
            before_hearts = work.native_ui_hearts_before, after_hearts = work.native_hearts_after });
        var archive = Path.Combine(root, "resolved-pending"); Directory.CreateDirectory(archive);
        File.Move(Path.Combine(root, "pending.json"), Path.Combine(archive, work.cmd.request_id + ".json"));
        SettleResponseBindings(work.cmd.request_id);
        NativeComparisonStage("native_order_server_confirmed", new { child_id = work.child_id, heart_debit_count = 1 });
        if (comparison)
        {
            nativeComparison.completed_trials++; nativeComparison.active_request_id = "";
            nativeComparison.state = "waiting_for_next_heart";
            nativeComparison.not_before = Now + Math.Max(nativeComparison.quiet_seconds, cfg.minimum_action_interval_seconds); SaveNativeComparison();
        }
        ReleaseRequestUi(work.cmd.request_id, ui);
        recovery = null; inFlight = false; uncertain = false; pendingId = "";
        serverInventoryIds = ids; inventoryAccountContext = AccountContext(); inventorySynced = true;
        serverInventoryVerifiedAt = Now; inventoryPacing.Healthy(); SavePacing();
        nextActionAt = comparison ? nativeComparison.not_before : Now + cfg.minimum_action_interval_seconds;
        // Close only the hidden result component used by our own comparison.
        // This is a game method, without desktop input or additional networking.
        if (nativeComparisonUi != null && !nativeComparisonUi.gameObject.activeInHierarchy) nativeComparisonUi.Close();
        RefreshGameViews(data, game);
        message = comparison ? $"Original native-order comparison confirmed ({nativeComparison.completed_trials}/{nativeComparison.maximum_trials})"
            : "Original game breeding result confirmed by delayed server inventory"; Status("ready");
    }
}
