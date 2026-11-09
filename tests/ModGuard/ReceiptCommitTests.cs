using PCFish.Automation;

internal static class ReceiptCommitTests
{
    internal static void Run(Action<bool, string> check)
    {
        var child = Guid.NewGuid().ToString();
        var before = new Snapshot { fish = [new() { id = "parent" }, new() { id = "other", locked = true }, new() { id = "basic", basic = true }] };
        var breed = new Command { kind = "breed", fish_ids = ["parent", "basic"] };
        check(!ReceiptCommit.InputsOwned(breed, before, ["other"]), "missing server-owned parent is rejected before sending a consumptive request");
        check(ReceiptCommit.InputsOwned(breed, before, ["parent", "other"]), "verified ordinary parent and local starter can be dispatched");
        var receipt = new ReceiptCommit(breed, before, child, "account");
        int applications = 0;
        check(!receipt.ApplyOnce(() => applications++), "success receipt cannot mutate local inventory before ownership proof");
        check(!receipt.Confirm(["parent", "other"], "account", 1), "missing server child remains pending without deleting the local cache");
        check(!receipt.Confirm(["parent", "other", child], "different-account", 2), "cross-account response cannot be committed");
        check(!receipt.Confirm(["parent", child], "account", 3), "unrelated server disappearance is recorded before commit");
        check(receipt.Confirm(["parent", "other", child], "account", 4), "server-owned child and preserved parents authorize commit");
        check(receipt.ApplyOnce(() => { applications++; receipt.ApplyOnce(() => applications++); }) && applications == 1, "nested duplicate native continuation applies exactly once");
        check(!receipt.ApplyOnce(() => applications++) && applications == 1, "late repeated callback cannot recreate a consumed fish");
        var cache = new HashSet<string> { "parent", "other" };
        var owned = new ReceiptCommit(breed, before, child, "account");
        owned.Confirm(["parent", "other", child], "account", 5);
        owned.ApplyOnce(() => cache.Add(child));
        cache.Remove(child); // A later legitimate merge consumes the offspring.
        check(!owned.ApplyOnce(() => cache.Add(child)) && !cache.Contains(child), "delayed duplicate receipt cannot resurrect a later-consumed UID in local inventory");
        var merge = new ReceiptCommit(new() { kind = "merge", fish_ids = ["parent"] }, before, child, "account");
        check(!merge.Confirm(["parent", "other", child], "account", 5), "server must confirm material removal before local merge commit");
        check(merge.Confirm(["other", child, "external-new-fish"], "account", 6), "verified merge permits unrelated legitimate server additions");
    }
}
