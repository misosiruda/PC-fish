using PCFish.Automation;

internal static class NetworkRecoveryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var p = new InventoryRequestPacing();
        check(p.Ready(100), "initial inventory read can start");
        p.Reserve(100);
        check(!p.Ready(114.999) && p.Ready(115), "all inventory reads share a 15 second minimum interval");
        foreach (var delay in new[] { 30, 60, 120, 240, 300, 300 })
        {
            double now = p.next_at;
            p.Defer(now);
            check(p.next_at == now + delay, "failed or unsettled inventory read backs off by " + delay);
        }
        var restored = new InventoryRequestPacing { next_at = p.next_at, failures = p.failures };
        check(!restored.Ready(p.next_at - 1), "restart preserves pending read cooldown");
        restored.Healthy(); restored.Defer(restored.next_at);
        check(restored.failures == 1, "confirmed healthy result resets backoff");
        var snapshot = new Snapshot { fish = [new() { id = "a" }, new() { id = "basic", basic = true }] };
        check(InventoryRequestPacing.CanReuse(["a"], "account", "account", 100, snapshot, 160), "recent server ownership proof avoids a duplicate preflight GET");
        check(!InventoryRequestPacing.CanReuse(["a"], "account", "account", 100, snapshot, 161), "expired ownership proof requires a fresh GET");
        check(!InventoryRequestPacing.CanReuse(["a"], "account", "other", 100, snapshot, 120), "another account cannot reuse ownership cache");
        check(!InventoryRequestPacing.CanReuse([], "account", "account", 100, snapshot, 120), "changed local inventory cannot reuse incomplete ownership cache");
        var recovery = new PendingRecovery { account_context_hash = "account" };
        check(recovery.CanRecover("account") && !recovery.CanRecover("other"), "restart recovery is tied to original account");
        recovery.accounting_started = true;
        check(!recovery.CanRecover("account"), "crash during accounting cannot debit hearts twice");
        recovery.accounting_saved = true;
        check(recovery.CanRecover("account"), "durably completed accounting can finish its journal without another debit");
        recovery.validation_failed = true;
        check(!recovery.CanRecover("account"), "actual post-commit validation failures are not cleared as network recovery");
        var saved = System.Text.Json.JsonSerializer.Deserialize<PendingRecovery>(System.Text.Json.JsonSerializer.Serialize(recovery))!;
        check(saved.accounting_saved && saved.validation_failed && !saved.CanRecover("account"), "restart retains accounting and actual validation boundaries");
        var fresh = new PendingRecovery { account_context_hash = "account" };
        fresh.before.tank_exp = 100;
        check(fresh.ExpTopUp(100, 0) == 20 && fresh.ExpTopUp(120, 0) == 0 && fresh.ExpTopUp(140, 0) == 0, "native server XP restoration is not awarded twice during recovery");
        int debits = 0, writes = 0, checkpoints = 0;
        fresh.AccountOnce(() => checkpoints++, () => { check(fresh.accounting_started, "accounting intent is durable before the heart debit"); debits++; }, () => writes++);
        check(fresh.accounting_saved && debits == 1 && writes == 1 && checkpoints == 2, "recovered result saves one debit between two checkpoints");
        var restarted = System.Text.Json.JsonSerializer.Deserialize<PendingRecovery>(System.Text.Json.JsonSerializer.Serialize(fresh))!;
        check(!restarted.AccountOnce(() => checkpoints++, () => debits++, () => writes++) && debits == 1, "restart after accounting save never charges the heart again");
        var crashed = new PendingRecovery { account_context_hash = "account" };
        try { crashed.AccountOnce(() => { }, () => debits++, () => throw new IOException("synthetic save failure")); } catch (IOException) { }
        check(crashed.accounting_started && !crashed.accounting_saved && !crashed.CanRecover("account"), "crash while saving heart cost remains explicit instead of replaying the charge");
        var outstanding = new PendingRecovery { account_context_hash = "account", child_id = Guid.NewGuid().ToString(), response_status = "success",
            cmd = new Command { kind = "breed", fish_ids = ["parent"] }, before = new Snapshot { fish = [new() { id = "parent" }] } };
        check(!outstanding.Receipt().Confirm(["parent"], "account", 100), "reachable server without the original child does not erase the outstanding breeding");
        var afterRestart = System.Text.Json.JsonSerializer.Deserialize<PendingRecovery>(System.Text.Json.JsonSerializer.Serialize(outstanding))!;
        check(afterRestart.CanRecover("account") && afterRestart.Receipt().Confirm(["parent", outstanding.child_id], "account", 300), "same original receipt can be verified after reconnect and restart");
        int recoveredDebits = 0;
        afterRestart.AccountOnce(() => { }, () => recoveredDebits++, () => { });
        check(afterRestart.accounting_saved && recoveredDebits == 1, "verified reconnect result completes exactly one outstanding breeding cost");
    }
}
