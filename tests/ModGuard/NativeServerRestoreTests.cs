using PCFish.Automation;

internal static class NativeServerRestoreTests
{
    internal static void Run(Action<bool, string> check)
    {
        string account = new('a', 64);
        (PendingRecovery w, Snapshot s, string[] ids) Fixture()
        {
            var basic = new FishState { id = "BF001", basic = true };
            var basic2 = new FishState { id = "BF002", basic = true };
            var retained = new FishState { id = "retained", remaining_breeds = 2, locked = true };
            var w = new PendingRecovery { cmd = new Command { kind = "breed", request_id = Guid.NewGuid().ToString(), fish_ids = ["BF001", "BF002"] },
                before = new Snapshot { fish = [basic, basic2, retained] }, flow = "native_breed", account_context_hash = account,
                child_id = Guid.NewGuid().ToString(), response_status = "success", local_commit_started = true,
                accounting_started = true, accounting_saved = true, native_completed_at = 100,
                native_ui_hearts_before = 1, native_hearts_after = 0 };
            var s = new Snapshot { captured_at = 161, hearts = 4, fish = [basic, basic2, retained, new() { id = w.child_id }, new() { id = "manual-result" }] };
            return (w, s, ["retained", "manual-result"]);
        }
        var (work, current, ids) = Fixture();
        check(NativeServerRestore.CanRestore(work, current, ids, account), "saved native cost plus delayed UID absence can enter original server restore");
        check(current.hearts == 4 && current.fish.Any(f => f.id == work.child_id), "eligibility does not alter resources or remove the result");
        work.validation_failed = true;
        work.note = "Original native breeding could not be verified after repeated delayed queries: Success receipt child is not yet present in server inventory";
        check(NativeServerRestore.CanRestore(work, current, ids, account), "existing 0.4.20 server-list stop can resume through native restoration");
        work.note = "Original native breeding could not be verified after repeated delayed queries: An unrelated server-owned fish disappeared during the request";
        check(!NativeServerRestore.CanRecheck(work), "missing unrelated server fish is not relabeled as the known missing-child checkpoint");
        foreach (string bad in new[] { "early", "account", "not_saved", "partial_cost", "legacy", "child_present", "unrelated_server_loss", "other_validation", "merge", "comparison" })
        {
            var (w, s, server) = Fixture();
            string a = account;
            switch (bad)
            {
                case "early": s.captured_at = 159.99; break;
                case "account": a = new('b', 64); break;
                case "not_saved": w.accounting_saved = false; break;
                case "partial_cost": w.accounting_started = false; break;
                case "legacy": w.legacy_accounting_unknown = true; break;
                case "child_present": server = [.. server, w.child_id]; break;
                case "unrelated_server_loss": server = ["manual-result"]; break;
                case "other_validation": w.validation_failed = true; w.note = "Unexpected resource loss"; break;
                case "merge": w.cmd.kind = "merge"; break;
                case "comparison": w.flow = "native_comparison"; break;
            }
            check(!NativeServerRestore.CanRestore(w, s, server, a), "restore is not authorized from unsafe/irrelevant evidence: " + bad);
        }
        var restored = new Snapshot { hearts = 4, fish = current.fish.Where(f => f.id != work.child_id).ToArray() };
        NativeServerRestore.Confirm(current, restored, ids, work.child_id);
        check(true, "native restoration may remove only the server-absent result while retaining basic fish, other server fish and locks");
        foreach (string loss in new[] { "heart", "lock", "basic", "server", "ghost" })
        {
            var after = new Snapshot { hearts = restored.hearts, fish = restored.fish.Select(f => new FishState { id = f.id, basic = f.basic, locked = f.locked }).ToArray() };
            if (loss == "heart") after.hearts--;
            if (loss == "lock") after.fish.Single(f => f.id == "retained").locked = false;
            if (loss == "basic") after.fish = after.fish.Where(f => f.id != "BF001").ToArray();
            if (loss == "server") after.fish = after.fish.Where(f => f.id != "retained").ToArray();
            if (loss == "ghost") after.fish = [.. after.fish, new() { id = work.child_id }];
            bool rejected = false;
            try { NativeServerRestore.Confirm(current, after, ids, work.child_id); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "native restore result checked for actual data loss/incompleteness: " + loss);
        }
        var retry = new NativeBreedRetry();
        retry.Queue(work.cmd.request_id, account, 200, 30);
        retry.Validate();
        check(retry.pending && retry.not_before == 230 && retry.consecutive_restores == 1, "first restored request schedules a new attempt after the existing action interval");
        retry.Queue(work.cmd.request_id, account, 210, 30);
        check(retry.not_before == 230 && retry.consecutive_restores == 1, "restarted restore checkpoint cannot multiply retries or costs");
        check(!retry.Dispatch(work.cmd.request_id, account), "original paid request ID can never be resent as the retry");
        check(!retry.Dispatch(Guid.NewGuid().ToString(), new('b',64)), "retry context cannot cross accounts");
        var next = Guid.NewGuid().ToString();
        check(retry.Dispatch(next, account) && !retry.pending && retry.next_request_id == next, "newly dispatched original-game request consumes only the scheduling marker");
        foreach (int delay in new[] { 60, 120, 240, 300, 300 })
        {
            retry.Queue(Guid.NewGuid().ToString(), account, 300, 30);
            check(retry.not_before == 300 + delay, "repeated server restoration backs off: " + delay);
            retry.Dispatch(Guid.NewGuid().ToString(), account);
        }
        retry.Confirmed(account);
        check(retry.consecutive_restores == 0 && !retry.pending, "a confirmed normal result resets only the retry schedule");
        var (crashed, canonical, serverIds) = Fixture();
        crashed.server_restore_started_at = 160;
        canonical.fish = canonical.fish.Where(f => f.id != crashed.child_id).ToArray();
        check(NativeServerRestore.CanRestore(crashed, canonical, serverIds, account), "crash after native restore can safely recheck and finish without another breeding debit");
    }
}
