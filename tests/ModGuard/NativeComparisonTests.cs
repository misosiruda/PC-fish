using PCFish.Automation;

internal static class NativeComparisonTests
{
    internal static void Run(Action<bool, string> check)
    {
        string account = new('a', 64);
        check(GameClock.NextHeartAt(1794130579, 420f) == 1794130999d, "real Unix timestamp keeps exact native seven-minute heart deadline");
        check(GameClock.NextHeartAt(1794130999, 420f) == 1794131419d, "second observed heart deadline does not round to float epochs");
        check(GameClock.NextHeartAt(1794130579, 0.5f) == 1794130579.5d, "fractional game intervals preserve epoch precision");
        var parent = new FishState { id = "parent", remaining_breeds = 3 };
        var starter = new FishState { id = "starter", basic = true };
        var locked = new FishState { id = "locked", locked = true, remaining_breeds = 2 };
        var native = new PendingRecovery { cmd = new Command { kind = "breed", fish_ids = ["parent", "starter"] },
            before = new Snapshot { fish = [parent, starter, locked] }, child_id = Guid.NewGuid().ToString(),
            accounting_saved = true, native_ui_hearts_before = 2, native_hearts_after = 1 };
        var grownParent = new FishState { id = "parent", remaining_breeds = 2, growth = 999 };
        var retainedLock = new FishState { id = "locked", locked = true, remaining_breeds = 2 };
        var observed = new Snapshot { hearts = 2, tank_exp = 500, fish = [grownParent, starter, retainedLock,
            new FishState { id = native.child_id }, new FishState { id = "natural-reward" }] };
        NativeBreedEvidence.ConfirmLocal(native, observed);
        check(true, "native validation allows growth, XP, rewards and heart regeneration while preserving one debit");
        foreach (string change in new[] { "duplicate_cost", "parent_not_used", "unrelated_used", "lock_removed", "child_missing" })
        {
            if (change == "duplicate_cost") native.native_hearts_after = 0;
            if (change == "parent_not_used") grownParent.remaining_breeds = 3;
            if (change == "unrelated_used") retainedLock.remaining_breeds = 1;
            if (change == "lock_removed") retainedLock.locked = false;
            if (change == "child_missing") observed.fish = observed.fish.Where(f => f.id != native.child_id).ToArray();
            try { NativeBreedEvidence.ConfirmLocal(native, observed); throw new Exception(change + " accepted"); }
            catch (InvalidDataException) { check(true, "actual native resource discrepancy rejected: " + change); }
            native.native_hearts_after = 1; grownParent.remaining_breeds = 2; retainedLock.remaining_breeds = 2; retainedLock.locked = true;
        }
        var p = new NativeComparisonPlan { enabled = true, comparison_id = Guid.NewGuid().ToString(),
            account_context_hash = account, expires_at = 1000, not_before = 100 };
        var s = new Snapshot { hearts = 1 };
        var cfg = new Settings { enabled = true, mode = "protected" };
        p.Validate(); check(p.CanStart(s, cfg, account, 100), "native comparison starts only after startup quiet interval");
        check(!p.CanStart(s, cfg, account, 99), "no PUT during startup quiet interval");
        check(!p.CanStart(s, cfg, "other", 100), "different account cannot start native comparison");
        s.hearts = 0; check(!p.CanStart(s, cfg, account, 100), "native comparison cannot invent a heart"); s.hearts = 1;
        s.request_in_flight = true; check(!p.CanStart(s, cfg, account, 100), "native comparison cannot overlap a request"); s.request_in_flight = false;
        s.uncertain = true; check(!p.CanStart(s, cfg, account, 100), "native comparison cannot repeat an uncertain request"); s.uncertain = false;
        s.ui_busy = true; check(!p.CanStart(s, cfg, account, 100), "native comparison respects live user UI"); s.ui_busy = false;
        cfg.enabled = false; check(!p.CanStart(s, cfg, account, 100), "pause stops new comparison requests"); cfg.enabled = true;
        cfg.mode = "observe"; check(!p.CanStart(s, cfg, account, 100), "observe never dispatches a comparison"); cfg.mode = "protected";
        check(!p.CanStart(s, cfg, account, 1000), "comparison expiration stops new PUTs");
        p.completed_trials = 6; check(!p.CanStart(s, cfg, account, 100), "trial budget bounds live consumption"); p.completed_trials = 0;
        p.active_request_id = Guid.NewGuid().ToString();
        check(!p.CanStart(s, cfg, account, 100), "persisted active request cannot be resent after restart");
        var work = new PendingRecovery { cmd = new Command { request_id = p.active_request_id },
            account_context_hash = account, flow = "native_comparison", native_completed_at = 200,
            accounting_saved = true, native_ui_hearts_before = 1, native_hearts_after = 0 };
        check(!p.CanRead(work, account, 259.999), "no added inventory query before sixty seconds after native save");
        check(p.CanRead(work, account, 260), "one delayed read becomes eligible after native save");
        check(!p.CanRead(work, "other", 260), "cross-account delayed evidence cannot complete comparison");
        work.flow = ""; check(!p.CanRead(work, account, 260), "legacy pending is not converted to a native comparison"); work.flow = "native_comparison";
        work.cmd.request_id = Guid.NewGuid().ToString(); check(!p.CanRead(work, account, 260), "another request's save cannot authorize a read"); work.cmd.request_id = p.active_request_id;
        work.native_completed_at = 0; check(!p.CanRead(work, account, 260), "missing native callback completion does not authorize early GET");
        p.quiet_seconds = 59;
        try { p.Validate(); throw new Exception("short quiet interval accepted"); }
        catch (InvalidDataException) { check(true, "invalid short quiet interval rejected"); }
        p.quiet_seconds = 60; p.maximum_trials = 21;
        try { p.Validate(); throw new Exception("unbounded comparison accepted"); }
        catch (InvalidDataException) { check(true, "unbounded live comparison rejected"); }
    }
}
