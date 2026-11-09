namespace PCFish.Automation;

internal static class NativeServerRestore
{
    internal static bool CanRecheck(PendingRecovery work) => work.flow == "native_breed"
        && (!work.validation_failed
            || work.note == "Original native breeding could not be verified after repeated delayed queries: Success receipt child is not yet present in server inventory");

    internal static bool CanRestore(PendingRecovery work, Snapshot current, string[] ids, string account)
    {
        if (!CanRecheck(work) || work.cmd.kind != "breed" || work.account_context_hash != account
            || work.account_context_hash.Length != 64 || work.legacy_accounting_unknown
            || work.response_status != "success" || !Guid.TryParse(work.child_id, out _)
            || work.before.fish.Any(f => f.id == work.child_id) || ids.Contains(work.child_id)
            || !work.local_commit_started || !work.accounting_started || !work.accounting_saved
            || work.native_completed_at <= 0 || current.captured_at < work.native_completed_at + 60
            || work.native_ui_hearts_before < 1 || work.native_hearts_after != work.native_ui_hearts_before - 1
            || current.hearts < work.native_hearts_after) return false;
        // Server restoration is authorized for this missing result. Unrelated
        // missing fish are a separate discrepancy, never silently discarded.
        if (work.before.fish.Any(f => !f.basic && !ids.Contains(f.id))
            || current.fish.Any(f => !f.basic && f.id != work.child_id && !ids.Contains(f.id))) return false;
        if (work.server_restore_started_at == 0)
            NativeBreedEvidence.ConfirmLocal(work, current);
        else
            foreach (var old in work.before.fish)
            {
                var retained = current.fish.SingleOrDefault(f => f.id == old.id);
                if (retained == null || old.locked && !retained.locked)
                    throw new InvalidDataException("Unrelated local data changed while restoring the server inventory");
            }
        return true;
    }

    internal static void Confirm(Snapshot beforeRestore, Snapshot restored, string[] serverIds, string childId)
    {
        InventorySyncEvidence.Confirm(beforeRestore, restored, serverIds);
        if (restored.fish.Any(f => f.id == childId))
            throw new InvalidDataException("Native server restoration did not remove the server-absent result");
    }
}

internal sealed class NativeBreedRetry
{
    public bool pending { get; set; }
    public string account_context_hash { get; set; } = "";
    public string restored_request_id { get; set; } = "";
    public string next_request_id { get; set; } = "";
    public int consecutive_restores { get; set; }
    public double not_before { get; set; }
    public void Validate()
    {
        if (consecutive_restores < 0 || consecutive_restores > 100000 || !double.IsFinite(not_before) || not_before < 0
            || account_context_hash.Length is not (0 or 64)
            || restored_request_id.Length > 0 && !Guid.TryParse(restored_request_id, out _)
            || next_request_id.Length > 0 && !Guid.TryParse(next_request_id, out _)
            || pending && (account_context_hash.Length != 64 || restored_request_id.Length == 0))
            throw new InvalidDataException("Invalid native breeding retry journal");
    }
    public void Queue(string request, string account, double now, double minimumInterval)
    {
        if (pending && restored_request_id == request && account_context_hash == account) return;
        consecutive_restores = account_context_hash == account ? Math.Min(100000, consecutive_restores + 1) : 1;
        account_context_hash = account; restored_request_id = request; next_request_id = ""; pending = true;
        not_before = now + Math.Max(minimumInterval, Math.Min(300, 30 * Math.Pow(2, Math.Min(4, consecutive_restores - 1))));
    }
    public bool Dispatch(string newRequest, string account)
    {
        if (!pending || account != account_context_hash || newRequest == restored_request_id || !Guid.TryParse(newRequest, out _)) return false;
        pending = false; next_request_id = newRequest; return true;
    }
    public void Confirmed(string account)
    {
        if (account != account_context_hash) return;
        pending = false; consecutive_restores = 0;
    }
}
