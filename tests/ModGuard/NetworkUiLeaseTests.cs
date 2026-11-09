using PCFish.Automation;

internal static class NetworkUiLeaseTests
{
    internal static void Run(Action<bool, string> check)
    {
        var lease = new NetworkUiLease();
        bool processing = false, visible = false; int restores = 0;
        void Restore(bool p, bool v) { processing = p; visible = v; restores++; }
        lease.Begin("breed", processing, visible);
        processing = visible = true;
        bool probePriorProcessing = processing, probePriorVisible = visible;
        processing = visible = true;
        processing = probePriorProcessing; visible = probePriorVisible;
        check(processing && visible, "a probe preserves the unresolved native request's UI state");
        check(!lease.Release("other", Restore) && processing && visible && restores == 0, "unrelated response cannot release the owned network UI");
        check(lease.Release("breed", Restore) && !processing && !visible && restores == 1, "nonblocking deferral releases the original breed spinner after its probe completes");
        check(!lease.Release("breed", Restore) && restores == 1, "duplicate completion restores the UI only once");
        lease.Begin("new-breed", false, false); processing = visible = true;
        check(!lease.Release("breed", Restore) && processing && lease.RequestId == "new-breed", "late old completion cannot clear a newer request");
        lease.Release("new-breed", Restore);
        lease.Begin("prior-busy", true, true);
        lease.Release("prior-busy", Restore);
        check(processing && visible, "preexisting external busy flags are preserved");
        lease.Begin("nested", false, false); int nested = 0;
        lease.Release("nested", (p, v) => { nested++; lease.Release("nested", (p2, v2) => nested++); });
        check(nested == 1 && lease.RequestId == "", "reentrant completion cannot restore twice");
        var restarted = new NetworkUiLease();
        check(!restarted.Release("old-session", Restore), "restart does not assume ownership of another request's UI");
        lease.Begin("active", false, false); bool rejected = false;
        try { lease.Begin("replacement", false, false); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && lease.RequestId == "active", "active ownership cannot be silently replaced");
        lease.Release("active", Restore);
    }
}
