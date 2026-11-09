namespace PCFish.Automation;

internal static class GameClock
{
    // long + float otherwise rounds contemporary Unix time to 128-second steps.
    internal static double NextHeartAt(long timestamp, float interval) => timestamp + (double)interval;
}

internal static class NativeBreedEvidence
{
    internal static void ConfirmLocal(PendingRecovery work, Snapshot current)
    {
        if (!work.accounting_saved || work.native_ui_hearts_before < 1
            || work.native_hearts_after != work.native_ui_hearts_before - 1
            || current.hearts < work.native_hearts_after || !current.fish.Any(f => f.id == work.child_id))
            throw new InvalidDataException("Original native breeding accounting/result evidence is incomplete");
        foreach (var before in work.before.fish)
        {
            var after = current.fish.SingleOrDefault(f => f.id == before.id)
                ?? throw new InvalidDataException("A pre-existing local fish disappeared during native breeding");
            if (before.locked && !after.locked) throw new InvalidDataException("Native breeding removed a lock");
            if (before.basic) continue;
            int used = work.cmd.fish_ids.Contains(before.id) ? 1 : 0;
            if (used == 1 ? after.remaining_breeds != before.remaining_breeds - 1 : after.remaining_breeds < before.remaining_breeds)
                throw new InvalidDataException("Native breeding uses differ from the selected parents");
        }
    }
}

// Explicit, bounded live comparison. This is not a general relaxation of the
// normal transaction guard and cannot select consumable parents.
internal sealed class NativeComparisonPlan
{
    public bool enabled { get; set; }
    public string comparison_id { get; set; } = "";
    public string account_context_hash { get; set; } = "";
    public int maximum_trials { get; set; } = 6;
    public int quiet_seconds { get; set; } = 60;
    public double expires_at { get; set; }
    public int completed_trials { get; set; }
    public string active_request_id { get; set; } = "";
    public double not_before { get; set; }
    public string state { get; set; } = "queued";
    public string note { get; set; } = "";

    internal void Validate()
    {
        if (!Guid.TryParse(comparison_id, out _) || account_context_hash.Length != 64
            || maximum_trials is < 1 or > 20 || quiet_seconds is < 60 or > 300
            || completed_trials < 0 || completed_trials > maximum_trials || !double.IsFinite(expires_at)
            || !double.IsFinite(not_before) || active_request_id.Length > 0 && !Guid.TryParse(active_request_id, out _))
            throw new InvalidDataException("Invalid native comparison plan");
    }

    internal bool CanStart(Snapshot s, Settings cfg, string account, double now)
        => enabled && completed_trials < maximum_trials && active_request_id.Length == 0 && now < expires_at
            && account == account_context_hash && now >= not_before && cfg.enabled && cfg.mode != "observe"
            && !s.request_in_flight && !s.uncertain && !s.ui_busy && s.hearts > 0;

    internal bool CanRead(PendingRecovery work, string account, double now)
        => enabled && active_request_id == work.cmd.request_id && work.flow == "native_comparison"
            && account == account_context_hash && account == work.account_context_hash
            && work.native_completed_at > 0 && now >= work.native_completed_at + quiet_seconds;
}
