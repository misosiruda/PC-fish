using PCFish.Automation;

internal static class InventorySyncTests
{
    internal static void Run(Action<bool, string> check)
    {
        var previous = new InventoryProbeTicket("old", "account", 1);
        var current = new InventoryProbeTicket("new", "account", 2);
        check(!previous.TryComplete(current), "delayed inventory GET cannot complete a newer probe");
        check(current.TryComplete(current) && !current.TryComplete(current), "inventory GET continuation completes at most once");
        var timedOut = new InventoryProbeTicket("timeout", "account", 3);
        check(timedOut.TryComplete(timedOut) && !timedOut.TryComplete(timedOut), "late network success after probe timeout cannot dispatch a second request");
        check(!previous.SameAccount("another-account") && previous.SameAccount("account"), "probe evidence is bound to the authenticated account");
        var ids = InventorySyncEvidence.ReadIds("""{"status":"success","data":{"items":[{"uid":"a"},{"uid":"b"}],"exp":100}}""");
        check(ids.SequenceEqual(new[] { "a", "b" }), "server inventory IDs parsed without account fields");
        check(InventorySyncEvidence.ReadIds("""{"status":"success","data":"{\"items\":\"[{\\\"uid\\\":\\\"a\\\"}]\"}"}""").SequenceEqual(new[] { "a" }), "native string-encoded inventory format supported");
        foreach (var response in new[] { """{"status":"fail"}""", """{"status":"success","data":{"items":[{"id":"wrong-key"}]}}""", """{"status":"success","data":{"items":[{"uid":"a"},{"uid":"a"}]}}""" })
        {
            bool rejected = false;
            try { InventorySyncEvidence.ReadIds(response); } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException) { rejected = true; }
            check(rejected, "failed or incomplete server inventory cannot authorize reconciliation");
        }
        var before = new Snapshot { hearts = 2, fish = [new() { id = "basic", basic = true }, new() { id = "a", locked = true }, new() { id = "stale" }] };
        var after = new Snapshot { hearts = 2, fish = [new() { id = "basic", basic = true }, new() { id = "a", locked = true }, new() { id = "b" }] };
        InventorySyncEvidence.Confirm(before, after, ids); check(true, "only server-absent local entries removed and server items restored");
        foreach (var change in new[] { "heart", "lock", "basic", "server", "stale" })
        {
            var bad = new Snapshot { hearts = after.hearts, fish = after.fish.Select(f => new FishState { id = f.id, basic = f.basic, locked = f.locked }).ToArray() };
            switch (change)
            {
                case "heart": bad.hearts--; break;
                case "lock": bad.fish[1].locked = false; break;
                case "basic": bad.fish = bad.fish[1..]; break;
                case "server": bad.fish = bad.fish[..2]; break;
                case "stale": bad.fish = [.. bad.fish, new() { id = "stale" }]; break;
            }
            bool rejected = false;
            try { InventorySyncEvidence.Confirm(before, bad, ids); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "incomplete reconciliation rejected: " + change);
        }
    }
}
