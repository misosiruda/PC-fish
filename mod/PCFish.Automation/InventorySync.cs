using System.Text.Json;

namespace PCFish.Automation;

internal sealed class InventoryProbeTicket(string id, string accountContext, double startedAt)
{
    internal string Id { get; } = id;
    internal string AccountContext { get; } = accountContext;
    internal double StartedAt { get; } = startedAt;
    bool completed;

    internal bool TryComplete(InventoryProbeTicket active)
    {
        if (completed || !ReferenceEquals(this, active)) return false;
        completed = true;
        return true;
    }

    internal bool SameAccount(string current) => AccountContext == current;
}

internal static class InventorySyncEvidence
{
    internal static string[] ReadIds(string response)
    {
        using var outer = JsonDocument.Parse(response);
        if (outer.RootElement.GetProperty("status").GetString() != "success") throw new InvalidDataException("Inventory query was not successful");
        var data = outer.RootElement.GetProperty("data");
        using var dataDoc = JsonDocument.Parse(data.ValueKind == JsonValueKind.String ? data.GetString()! : data.GetRawText());
        var items = dataDoc.RootElement.GetProperty("items");
        using var itemsDoc = JsonDocument.Parse(items.ValueKind == JsonValueKind.String ? items.GetString()! : items.GetRawText());
        var ids = itemsDoc.RootElement.EnumerateArray().Select(item => item.GetProperty("uid").GetString() ?? "").ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Invalid server inventory IDs");
        return ids;
    }

    internal static void Confirm(Snapshot before, Snapshot after, string[] serverIds)
    {
        var server = serverIds.ToHashSet(StringComparer.Ordinal);
        var current = new Dictionary<string, FishState>(StringComparer.Ordinal);
        foreach (var fish in after.fish) current.Add(fish.id, fish);
        if (after.hearts < before.hearts) throw new InvalidDataException("Inventory query consumed a heart");
        foreach (var fish in before.fish)
        {
            bool keep = fish.basic || server.Contains(fish.id);
            if (keep != current.ContainsKey(fish.id)) throw new InvalidDataException("Local reconciliation differs from server inventory");
            if (keep && fish.locked && !current[fish.id].locked) throw new InvalidDataException("Inventory sync removed a lock");
        }
        if (server.Any(id => !current.ContainsKey(id)) || after.fish.Any(f => !f.basic && !server.Contains(f.id)))
            throw new InvalidDataException("Reconciled inventory is incomplete");
    }
}
