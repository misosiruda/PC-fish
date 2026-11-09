using System.Text.Json;
using PCFish.Automation;

internal static class DeferredReceiptTests
{
    internal static void Run(Action<bool, string> check)
    {
        PendingRecovery Work() => new() { account_context_hash = "account", response_status = "success", child_id = Guid.NewGuid().ToString(),
            cmd = new() { kind = "breed", fish_ids = ["BF001", "BF002"] },
            before = new() { hearts = 1, fish = [new() { id = "BF001", basic = true }, new() { id = "BF002", basic = true },
                new() { id = "owned", remaining_breeds = 1, locked = true }] } };
        Snapshot Current(PendingRecovery work) => JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(work.before))!;
        var w = Work(); var current = Current(w); current.hearts = 5;
        check(!w.ObserveUnappliedBasic(["owned"], current, "account", 600), "one healthy unchanged read cannot release a missing success receipt");
        check(!w.ObserveUnappliedBasic(["owned"], current, "account", 630), "two reads cannot release a missing success receipt");
        check(w.ObserveUnappliedBasic(["owned"], current, "account", 720), "repeated unchanged starter breeding may leave the blocking queue after adequately spaced evidence");
        check(!w.accounting_started && !w.accounting_saved && !w.local_commit_started, "deferral does not invent a child or debit a heart");
        var restart = JsonSerializer.Deserialize<PendingRecovery>(JsonSerializer.Serialize(w))!;
        check(restart.absent_confirmations == 3 && restart.ObserveUnappliedBasic(["owned"], current, "account", 730), "restart preserves the mature unchanged inventory evidence");
        check(restart.absent_confirmations == 3, "repeated callbacks inside 30 seconds cannot inflate the evidence count");
        foreach (var change in new[] { "ordinary", "merge", "duplicate", "account", "status", "uid", "server_loss", "server_extra", "uses", "lock", "hearts", "commit", "accounting", "saved", "validation", "local_child", "server_child" })
        {
            var bad = Work(); var now = Current(bad); var ids = new[] { "owned" }; var account = "account";
            switch (change)
            {
                case "ordinary": bad.before.fish[0].basic = false; now.fish[0].basic = false; break;
                case "merge": bad.cmd.kind = "merge"; break;
                case "duplicate": bad.cmd.fish_ids = ["BF001", "BF001"]; break;
                case "account": account = "another"; break;
                case "status": bad.response_status = "unknown"; break;
                case "uid": bad.child_id = ""; break;
                case "server_loss": ids = []; break;
                case "server_extra": ids = ["owned", "new-server-item"]; break;
                case "uses": now.fish[2].remaining_breeds--; break;
                case "lock": now.fish[2].locked = false; break;
                case "hearts": now.hearts = 0; break;
                case "commit": bad.local_commit_started = true; break;
                case "accounting": bad.accounting_started = true; break;
                case "saved": bad.accounting_saved = true; break;
                case "validation": bad.validation_failed = true; break;
                case "local_child": now.fish = [.. now.fish, new() { id = bad.child_id }]; break;
                case "server_child": ids = ["owned", bad.child_id]; break;
            }
            bad.ObserveUnappliedBasic(ids, now, account, 600);
            bad.ObserveUnappliedBasic(ids, now, account, 630);
            check(!bad.ObserveUnappliedBasic(ids, now, account, 720), "unapplied starter exception retains real consumption/uncertainty: " + change);
        }
        var early = Work(); var earlyCurrent = Current(early);
        early.ObserveUnappliedBasic(["owned"], earlyCurrent, "account", 10);
        early.ObserveUnappliedBasic(["owned"], earlyCurrent, "account", 40);
        check(!early.ObserveUnappliedBasic(["owned"], earlyCurrent, "account", 50), "three callbacks without adequate spacing cannot detach a young request");
        var bounded = Work(); var boundedCurrent = Current(bounded);
        bounded.ObserveUnappliedBasic(["owned"], boundedCurrent, "account", 10);
        bounded.ObserveUnappliedBasic(["owned"], boundedCurrent, "account", 40);
        check(bounded.ObserveUnappliedBasic(["owned"], boundedCurrent, "account", 70), "a verified nonconsumptive starter receipt does not block all breeding for five minutes");
        var late = w.Receipt();
        check(late.Confirm(["owned", w.child_id], "account", 800), "deferred receipt still accepts positive server ownership later");
        check(w.CanSettleLate([w.child_id, "later-merge-result"], "account"), "late starter result can settle after unrelated inventory legitimately changed");
        check(!w.CanSettleLate([], "account") && !w.CanSettleLate([w.child_id], "other"), "late cost settlement requires actual child ownership in the original account");
        int charges = 0;
        w.AccountOnce(() => { }, () => charges++, () => { });
        w.AccountOnce(() => { }, () => charges++, () => { });
        check(charges == 1, "late arrival after nonblocking deferral charges the original heart exactly once");
    }
}
