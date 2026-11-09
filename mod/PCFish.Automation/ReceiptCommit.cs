namespace PCFish.Automation;

internal sealed class ReceiptCommit
{
    internal static bool InputsOwned(Command cmd, Snapshot snapshot, IEnumerable<string> serverIds)
    {
        var server = serverIds.ToHashSet(StringComparer.Ordinal);
        return cmd.fish_ids.All(id => snapshot.fish.Any(f => f.id == id && (f.basic || server.Contains(id))));
    }

    readonly HashSet<string> expected;
    readonly string[] consumed;
    public string ChildId { get; }
    public string AccountContext { get; }
    public bool Confirmed { get; private set; }
    public bool Applied { get; private set; }
    public double ConfirmedAt { get; private set; }
    public string Note { get; private set; } = "Awaiting server ownership confirmation";

    internal ReceiptCommit(Command cmd, Snapshot before, string childId, string accountContext)
    {
        if (!Guid.TryParse(childId, out _) || before.fish.Any(f => f.id == childId))
            throw new InvalidDataException("Response child UID is missing or already owned");
        ChildId = childId; AccountContext = accountContext;
        consumed = cmd.kind is "merge" or "craft" ? cmd.fish_ids : [];
        expected = before.fish.Where(f => !f.basic && !consumed.Contains(f.id)).Select(f => f.id).ToHashSet(StringComparer.Ordinal);
        expected.Add(childId);
    }

    internal bool Confirm(IEnumerable<string> serverIds, string accountContext, double now)
    {
        if (accountContext != AccountContext) { Note = "Account context changed while the request was outstanding"; return false; }
        var actual = serverIds.ToHashSet(StringComparer.Ordinal);
        if (!actual.Contains(ChildId)) { Note = "Success receipt child is not yet present in server inventory"; return false; }
        if (consumed.Any(actual.Contains)) { Note = "Server still reports consumed materials"; return false; }
        if (!expected.IsSubsetOf(actual)) { Note = "An unrelated server-owned fish disappeared during the request"; return false; }
        Confirmed = true; ConfirmedAt = now; Note = "Server ownership and material transition confirmed";
        return true;
    }

    internal bool ApplyOnce(Action apply)
    {
        if (!Confirmed || Applied) return false;
        // Mark before entering native code: a nested/duplicate callback must not
        // apply the same receipt twice, even if the native continuation throws.
        Applied = true; apply(); return true;
    }
}
