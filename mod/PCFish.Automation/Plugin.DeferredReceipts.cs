using System.Text.Json;
using NN.PF.Core;
using NN.PF.Core.Managers;

namespace PCFish.Automation;

public sealed partial class Plugin
{
    bool absenceHistoryLoaded;
    double nextDeferredCheck;
    string deferredReceiptNote = "";
    string DeferredFolder => Path.Combine(root, "deferred-receipts");
    int DeferredReceiptCount() => Directory.Exists(DeferredFolder) ? Directory.GetFiles(DeferredFolder, "*.json").Length : 0;

    void SeedAbsenceHistory(Snapshot current)
    {
        if (absenceHistoryLoaded || recovery == null) return;
        absenceHistoryLoaded = true;
        var evidence = new List<(double at, string[] ids)>();
        var expected = recovery.before.fish.Select(f => f.id).ToHashSet(StringComparer.Ordinal);
        var folder = Path.Combine(root, "inventory-probes");
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadText(path));
                var item = doc.RootElement;
                var at = item.GetProperty("captured_at").GetDouble();
                if (at < recovery.dispatched_at || !item.TryGetProperty("account_context_hash", out var account)
                    || account.GetString() != recovery.account_context_hash) continue;
                var before = item.GetProperty("local_before").EnumerateArray().Select(v => v.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
                var after = item.GetProperty("local_after").EnumerateArray().Select(v => v.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
                if (!expected.SetEquals(before) || !expected.SetEquals(after)) continue;
                var ids = item.GetProperty("server_ids").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                evidence.Add((at, ids));
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { /* optional old evidence */ }
        }
        foreach (var item in evidence.OrderBy(e => e.at))
            recovery.ObserveUnappliedBasic(item.ids, current, AccountContext(), item.at);
    }

    bool TryDetachUnappliedBasic(string[] ids, Snapshot current, GameDataManager data, GameManager game, Settings cfg)
    {
        if (recovery == null) return false;
        SeedAbsenceHistory(current);
        bool proven = recovery.ObserveUnappliedBasic(ids, current, AccountContext(), Now);
        SaveRecovery();
        if (!proven) return false;
        // These two starter parents have no consumable server breeding uses.
        // Keep the receipt live in a separate ledger instead of inventing a
        // failure, resending the old PUT, or blocking every future action.
        Directory.CreateDirectory(DeferredFolder);
        recovery.note = "Repeated unchanged server inventory; unapplied starter receipt tracked separately";
        Atomic(Path.Combine(DeferredFolder, recovery.cmd.request_id + ".json"), recovery);
        nextDeferredCheck = Now + 300;
        deferredReceiptNote = recovery.note;
        FinishRecoveredRequest("server_absent_deferred", ids, current, data, game, cfg);
        return true;
    }

    IEnumerable<(string path, PendingRecovery work)> DeferredReceipts()
    {
        if (!Directory.Exists(DeferredFolder)) yield break;
        foreach (var path in Directory.GetFiles(DeferredFolder, "*.json"))
        {
            PendingRecovery work = null;
            try { work = JsonSerializer.Deserialize<PendingRecovery>(ReadText(path)); }
            catch (Exception ex) when (ex is IOException or JsonException) { deferredReceiptNote = "Deferred receipt retained; evidence could not be read"; }
            if (work != null && work.BasicParentsOnly() && work.account_context_hash == AccountContext()) yield return (path, work);
        }
    }

    bool TryCheckDeferredReceipts(Snapshot snapshot, UIManager ui)
    {
        if (!inventorySynced || inFlight || inventorySyncing || recovery != null || snapshot.ui_busy || !inventoryPacing.Ready(Now)) return false;
        var work = DeferredReceipts().ToArray();
        if (work.Length == 0 || Now < nextDeferredCheck && !work.Any(e => e.work.late_child_seen && snapshot.hearts > 0)) return false;
        if (InventoryRequestPacing.CanReuse(serverInventoryIds, inventoryAccountContext, AccountContext(), serverInventoryVerifiedAt, snapshot, Now))
        {
            ObserveDeferredReceipts(serverInventoryIds, ui);
            return false;
        }
        nextDeferredCheck = Now + 300;
        StartReadOnlyProbe(Guid.NewGuid().ToString(), snapshot, ui);
        return true;
    }

    void ObserveDeferredReceipts(string[] ids, UIManager ui)
    {
        nextDeferredCheck = Now + 300;
        foreach (var entry in DeferredReceipts().ToArray())
        {
            var work = entry.work;
            if (!ids.Contains(work.child_id)) continue;
            work.late_child_seen = true; Atomic(entry.path, work);
            if (inFlight || recovery != null || uncertain || !work.CanSettleLate(ids, AccountContext()))
            { deferredReceiptNote = "Late server result retained until active work/accounting is settled"; continue; }
            var data = UnityEngine.Object.FindObjectOfType<GameDataManager>();
            var game = UnityEngine.Object.FindObjectOfType<GameManager>();
            var raw = ReadText(configFile); var cfg = JsonSerializer.Deserialize<Settings>(raw);
            if (!cfg.enabled || cfg.mode == "observe") continue;
            var before = Capture(data, ui, Digest(raw), cfg);
            if (!before.fish.Any(f => f.id == work.child_id) && applyVerifiedProbeRestore == null) continue;
            bool restoreVerified = false;
            try
            {
                if (applyVerifiedProbeRestore != null) applyVerifiedProbeRestore();
                var after = Capture(data, ui, before.config_digest, cfg);
                InventorySyncEvidence.Confirm(before, after, ids);
                if (!after.fish.Any(f => f.id == work.child_id)) throw new InvalidDataException("Late verified result was not restored");
                restoreVerified = true;
                var events = Path.Combine(root, "late-receipt-events"); Directory.CreateDirectory(events);
                Atomic(Path.Combine(events, work.cmd.request_id + ".json"), new { request_id = work.cmd.request_id, child_id = work.child_id,
                    confirmed_at = Now, mutation_resent = false, server_ids = ids, before_hearts = before.hearts, after_hearts = data.breedCount,
                    custom_accounting_applied = false });
                var settled = Path.Combine(root, "settled-deferred-receipts"); Directory.CreateDirectory(settled);
                File.Move(entry.path, Path.Combine(settled, Path.GetFileName(entry.path)));
                deferredReceiptNote = "Late server result restored by the original game; no cost applied by the mod";
                serverInventoryIds = ids; serverInventoryVerifiedAt = Now;
                nextActionAt = Now + cfg.minimum_action_interval_seconds;
                RefreshGameViews(data, game);
            }
            catch (Exception ex)
            {
                deferredReceiptNote = "Late receipt retained: " + ex.GetType().Name;
                Atomic(entry.path, work);
                if (!restoreVerified)
                {
                    uncertain = true;
                    Atomic(Path.Combine(root, "consumption-alert.json"), new { captured_at = Now, request_id = work.cmd.request_id, reason = deferredReceiptNote });
                }
            }
        }
    }
}
