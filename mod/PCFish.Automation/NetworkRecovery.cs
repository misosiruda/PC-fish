namespace PCFish.Automation;

internal sealed class InventoryRequestPacing
{
    public double next_at { get; set; }
    public int failures { get; set; }
    public double last_completed_at { get; set; }
    public bool Ready(double now) => now >= next_at;
    public void Reserve(double now) => next_at = Math.Max(next_at, now + 15);
    public void Healthy() => failures = 0;
    public void Defer(double now)
    {
        failures = Math.Min(failures + 1, 5);
        next_at = Math.Max(next_at, now + Math.Min(300, 15 * Math.Pow(2, failures)));
    }
    public static bool CanReuse(string[] ids, string account, string currentAccount, double verifiedAt, Snapshot snapshot, double now)
        => ids != null && account == currentAccount && verifiedAt > 0 && now >= verifiedAt && now - verifiedAt <= 60
            && snapshot.fish.All(f => f.basic || ids.Contains(f.id));
}

internal sealed class PendingRecovery
{
    public Command cmd { get; set; } = new();
    public Snapshot before { get; set; } = new();
    public double dispatched_at { get; set; }
    public string account_context_hash { get; set; } = "";
    public string child_id { get; set; } = "";
    public string response_status { get; set; } = "";
    public bool local_commit_started { get; set; }
    public bool accounting_started { get; set; }
    public bool accounting_saved { get; set; }
    public bool legacy_accounting_unknown { get; set; }
    public bool validation_failed { get; set; }
    public string flow { get; set; } = "";
    public double native_completed_at { get; set; }
    public int native_ui_hearts_before { get; set; } = -1;
    public int native_hearts_after { get; set; } = -1;
    public double server_restore_started_at { get; set; }
    public double server_restore_completed_at { get; set; }
    public string server_restore_probe_id { get; set; } = "";
    public int absent_confirmations { get; set; }
    public double absence_first_at { get; set; }
    public double absence_last_at { get; set; }
    public bool late_child_seen { get; set; }
    public string note { get; set; } = "Awaiting authoritative result; mutation will not be resent";

    public bool CanRecover(string account) => !validation_failed && account_context_hash.Length != 0 && account_context_hash == account
        && (!accounting_started || accounting_saved);

    public ReceiptCommit Receipt() => new(cmd, before, child_id, account_context_hash);
    public int ExpTopUp(int current, int basicParents) => Math.Max(0, before.tank_exp + (2 - basicParents) * 10 - current);

    public bool BasicParentsOnly() => cmd.kind == "breed" && cmd.fish_ids.Length == 2 && cmd.fish_ids.Distinct().Count() == 2
        && cmd.fish_ids.All(id => before.fish.Any(f => f.id == id && f.basic));

    public bool CanSettleLate(string[] serverIds, string account) => CanRecover(account) && BasicParentsOnly()
        && response_status == "success" && Guid.TryParse(child_id, out _) && serverIds.Contains(child_id);

    public bool ObserveUnappliedBasic(string[] serverIds, Snapshot current, string account, double now)
    {
        bool unchanged = now >= dispatched_at && CanRecover(account) && BasicParentsOnly() && response_status == "success" && Guid.TryParse(child_id, out _)
            && !local_commit_started && !accounting_started && !accounting_saved && !serverIds.Contains(child_id)
            && !current.fish.Any(f => f.id == child_id) && RequestOutcomeGuard.HasNoConsumption(before, current)
            && before.fish.Where(f => !f.basic).Select(f => f.id).ToHashSet(StringComparer.Ordinal).SetEquals(serverIds);
        if (!unchanged) { absent_confirmations = 0; absence_first_at = 0; absence_last_at = 0; return false; }
        if (absent_confirmations == 0) { absence_first_at = now; absence_last_at = now; absent_confirmations = 1; }
        else if (now - absence_last_at >= 30) { absence_last_at = now; absent_confirmations++; }
        return absent_confirmations >= 3 && now - absence_first_at >= 60 && now - dispatched_at >= 60;
    }

    public bool AccountOnce(Action checkpoint, Action apply, Action save)
    {
        if (accounting_saved) return false;
        if (accounting_started) throw new InvalidDataException("Accounting checkpoint is unresolved");
        accounting_started = true; checkpoint();
        apply(); save();
        accounting_saved = true; checkpoint();
        return true;
    }
}
