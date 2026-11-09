namespace PCFish.Automation;

// Settled callbacks retain request identity and a weak lifetime check, never
// the aquarium snapshot or the strong native callback root.
internal sealed class ResponseBindings<T> : IDisposable where T : class
{
    internal sealed class Entry(string requestId, T payload, Func<bool> alive, Action release)
    {
        internal string RequestId { get; } = requestId;
        internal T Payload { get; private set; } = payload;
        internal bool Settled => Payload == null;
        internal bool Alive() => alive();
        internal void Settle() => Payload = default!;
        internal void Release() { Settle(); release(); }
    }

    readonly Dictionary<long, Entry> entries = new();
    internal int Count => entries.Count;
    internal int ActiveCount => entries.Values.Count(e => !e.Settled);

    internal void Register(long key, string requestId, T payload, Func<bool> alive, Action release)
    {
        PruneSettled();
        if (entries.Remove(key, out var old)) old.Release();
        entries.Add(key, new(requestId, payload, alive, release));
    }

    internal bool TryGet(long key, out Entry entry)
    {
        if (!entries.TryGetValue(key, out entry!)) return false;
        if (entry.Alive()) return true;
        entries.Remove(key); entry.Release(); entry = default!;
        return false;
    }

    internal void Settle(string requestId)
    {
        foreach (var entry in entries.Values)
            if (entry.RequestId == requestId) entry.Settle();
    }

    internal void PruneSettled()
    {
        foreach (var key in entries.Where(p => p.Value.Settled && !p.Value.Alive()).Select(p => p.Key).ToArray())
        { var entry = entries[key]; entries.Remove(key); entry.Release(); }
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values) entry.Release();
        entries.Clear();
    }
}
