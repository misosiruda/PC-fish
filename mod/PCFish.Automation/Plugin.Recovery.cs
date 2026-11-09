using System.Text.Json;
using NN.PF.Core;
using NN.PF.Core.Managers;
using NN.PF.Core.Util;

namespace PCFish.Automation;

public sealed partial class Plugin
{
    InventoryRequestPacing inventoryPacing = new();
    PendingRecovery recovery;
    double serverInventoryVerifiedAt;
    Il2CppSystem.Action<string> probeNativeRestore;
    Action applyVerifiedProbeRestore;

    string RecoveryPath => Path.Combine(root, "receipt-recovery", recovery.cmd.request_id + ".json");
    void SaveRecovery()
    {
        if (recovery == null) return;
        Directory.CreateDirectory(Path.Combine(root, "receipt-recovery"));
        Atomic(RecoveryPath, recovery);
    }

    void LoadNetworkRecovery()
    {
        var pacing = Path.Combine(root, "inventory-request-pacing.json");
        if (File.Exists(pacing))
        {
            var saved = JsonSerializer.Deserialize<InventoryRequestPacing>(ReadText(pacing));
            if (saved != null && double.IsFinite(saved.next_at) && saved.failures is >= 0 and <= 5) inventoryPacing = saved;
        }
        var pending = Path.Combine(root, "pending.json");
        if (!File.Exists(pending)) return;
        recovery = JsonSerializer.Deserialize<PendingRecovery>(ReadText(pending));
        if (recovery == null || !Guid.TryParse(recovery.cmd.request_id, out _)) return;
        if (File.Exists(RecoveryPath)) recovery = JsonSerializer.Deserialize<PendingRecovery>(ReadText(RecoveryPath));
        else
        {
            // Old journals have no accounting checkpoint. A pre-commit legacy
            // receipt may be migrated only when its child is still absent locally.
            recovery.legacy_accounting_unknown = true;
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            var events = Path.Combine(root, "events");
            if (Directory.Exists(events)) foreach (var path in Directory.GetFiles(events, "*.json"))
            {
                using var e = JsonDocument.Parse(ReadText(path));
                if (!e.RootElement.TryGetProperty("session", out var eventSession) || eventSession.GetString() != recovery.cmd.session) continue;
                var proof = Path.Combine(root, "receipt-confirmations", e.RootElement.GetProperty("event_id").GetString() + ".json");
                if (!File.Exists(proof)) continue;
                using var c = JsonDocument.Parse(ReadText(proof));
                if (c.RootElement.TryGetProperty("account_context_hash", out var context) && !string.IsNullOrEmpty(context.GetString())) accounts.Add(context.GetString());
            }
            if (recovery.account_context_hash.Length == 0 && accounts.Count == 1) recovery.account_context_hash = accounts.Single();
        }
        var response = Path.Combine(root, "network-responses", recovery.cmd.request_id + ".json");
        if (File.Exists(response) && recovery.response_status != "success")
        {
            using var doc = JsonDocument.Parse(ReadText(response));
            var diagnostic = doc.RootElement.GetProperty("diagnostic");
            recovery.response_status = diagnostic.GetProperty("status").GetString() ?? "";
            if (diagnostic.TryGetProperty("child_uid", out var child)) recovery.child_id = child.GetString() ?? "";
        }
        pendingId = recovery.cmd.request_id; pendingKind = recovery.cmd.kind; dispatchedAt = recovery.dispatched_at;
    }

    void SavePacing() => Atomic(Path.Combine(root, "inventory-request-pacing.json"), inventoryPacing);
    void ReserveInventoryRead(string purpose, string id)
    {
        if (!inventoryPacing.Ready(Now)) throw new InvalidOperationException("Inventory read interval has not elapsed");
        inventoryPacing.Reserve(Now); SavePacing();
        try
        {
            var folder = Path.Combine(root, "network-checks"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, id + ".json"), new { request_id = id, purpose, captured_at = Now, session });
        }
        catch (IOException) { /* optional audit cannot cancel a reserved read */ }
    }
    void DeferInventoryRead() { inventoryPacing.Defer(Now); SavePacing(); }
    void BeginAccounting()
    {
        if (recovery == null) return;
        if (recovery.accounting_started && !recovery.accounting_saved) throw new InvalidDataException("Accounting checkpoint is unresolved");
        recovery.accounting_started = true; SaveRecovery();
    }

    void ReleaseRequestUi(string requestId, UIManager ui)
    {
        bool released = requestUiLease.Release(requestId, (processing, visible) => {
            ui.isProcessing = processing;
            if (ui.viewNetwork != null) ui.viewNetwork.SetActive(visible);
        });
        if (!released) return;
        try
        {
            var folder = Path.Combine(root, "network-ui-release-events"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, requestId + ".json"), new { request_id = requestId, released_at = Now, session,
                ui_is_processing = ui.isProcessing, network_indicator_visible = ui.viewNetwork != null && ui.viewNetwork.activeInHierarchy });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* optional UI audit */ }
    }

    bool TryRecoverPending(Snapshot snapshot, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        if (recovery == null || !File.Exists(Path.Combine(root, "pending.json")) || inFlight && Now - dispatchedAt <= 60) return false;
        if (File.Exists(Path.Combine(root, "consumption-alert.json"))) return false;
        uncertain = true;
        if (!recovery.CanRecover(AccountContext()))
        {
            recovery.note = "Recovery needs the original account and an unambiguous accounting checkpoint";
            message = recovery.note; Status("uncertain"); return true;
        }
        if (recovery.legacy_accounting_unknown && snapshot.fish.Any(f => f.id == recovery.child_id))
        {
            recovery.note = "Legacy child is already local; prior heart accounting cannot be assumed";
            message = recovery.note; Status("uncertain"); return true;
        }
        if (!File.Exists(RecoveryPath)) SaveRecovery();
        if (!snapshot.network_busy && !snapshot.confirmation_open && inventoryPacing.Ready(Now))
            StartReadOnlyProbe(Guid.NewGuid().ToString(), snapshot, ui, ids => RecoverFromInventory(ids, data, game, ui, cfg));
        message = recovery?.note ?? "Recovered request";
        Status(recovery == null ? "ready" : "waiting_network");
        return true;
    }

    void RecoverFromInventory(string[] ids, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        if (recovery == null) return;
        if (ids == null)
        { recovery.absent_confirmations = 0; recovery.absence_first_at = 0; recovery.absence_last_at = 0; SaveRecovery(); return; }
        try
        {
            if (!recovery.CanRecover(AccountContext())) return;
            var current = Capture(data, ui, Digest(ReadText(configFile)), cfg);
            if (recovery.response_status == "fail" && RequestOutcomeGuard.CanConfirmRejection(true, "", recovery.before, current)
                && current.fish.Where(f => !f.basic).Select(f => f.id).ToHashSet(StringComparer.Ordinal).SetEquals(ids))
            {
                FinishRecoveredRequest("rejected_without_consumption", ids, current, data, game, cfg);
                return;
            }
            if (recovery.response_status != "success" || !Guid.TryParse(recovery.child_id, out _))
            {
                recovery.note = "Server is reachable; original outcome is not yet provable. Mutation is not resent";
                DeferInventoryRead(); SaveRecovery(); return;
            }
            var proof = recovery.Receipt();
            if (!proof.Confirm(ids, AccountContext(), Now))
            {
                if (TryDetachUnappliedBasic(ids, current, data, game, cfg)) return;
                recovery.note = proof.Note; DeferInventoryRead(); SaveRecovery(); return;
            }
            var before = Capture(data, ui, Digest(ReadText(configFile)), cfg);
            // The closure is scoped to the exact read-only GET just verified.
            // Reuse its native restore callback; do not send a second GET/PUT.
            recovery.local_commit_started = true; SaveRecovery();
            applyVerifiedProbeRestore();
            var restored = Capture(data, ui, before.config_digest, cfg);
            InventorySyncEvidence.Confirm(before, restored, ids);
            if (!restored.fish.Any(f => f.id == recovery.child_id)) throw new InvalidDataException("Verified child was not restored");
            // The original ManageRestore callback owns the reconciliation.
            // Never synthesize late XP, heart costs or a recharge timestamp.
            FinishRecoveredRequest("server_confirmed_after_reconnect", ids, before, data, game, cfg);
        }
        catch (Exception ex)
        {
            uncertain = true; inFlight = false;
            if (recovery != null) { recovery.note = "Recovery retained: " + ex.GetType().Name; SaveRecovery(); }
            message = recovery?.note ?? "Recovery retained"; DeferInventoryRead(); Status("uncertain");
        }
    }

    void FinishRecoveredRequest(string outcome, string[] ids, Snapshot before, GameDataManager data, GameManager game, Settings cfg)
    {
            var folder = Path.Combine(root, "recovery-events"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, recovery.cmd.request_id + ".json"), new { event_id = recovery.cmd.request_id,
                kind = recovery.cmd.kind, outcome, child_id = recovery.child_id,
                confirmed_at = Now, mutation_resent = false, server_ids = ids, before_hearts = before.hearts, after_hearts = data.breedCount,
                original_native_cost_saved = recovery.accounting_saved, custom_accounting_applied = false,
                server_restore_started_at = recovery.server_restore_started_at, server_restore_completed_at = recovery.server_restore_completed_at });
            var archive = Path.Combine(root, "resolved-pending"); Directory.CreateDirectory(archive);
            File.Move(Path.Combine(root, "pending.json"), Path.Combine(archive, recovery.cmd.request_id + ".json"));
            SettleResponseBindings(recovery.cmd.request_id);
            ReleaseRequestUi(recovery.cmd.request_id, UnityEngine.Object.FindObjectOfType<UIManager>());
            recovery = null; receipt = null; receiptContinuation = null;
            inFlight = false; uncertain = false; pendingId = "";
            serverInventoryIds = ids; serverInventoryVerifiedAt = Now;
            inventoryAccountContext = AccountContext(); inventorySynced = true;
            inventoryPacing.Healthy(); SavePacing();
            nextActionAt = Now + cfg.minimum_action_interval_seconds;
            RefreshGameViews(data, game);
            message = "Outstanding result verified; automation resumed without resending the mutation"; Status("ready");
    }
}
