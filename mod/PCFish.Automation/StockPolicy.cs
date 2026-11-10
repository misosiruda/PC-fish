namespace PCFish.Automation;

internal sealed class SpeciesInfo
{
    public string species { get; set; } = "";
    public string name { get; set; } = "";
    public int level { get; set; }
    public bool seasonal { get; set; }
}

internal sealed class StockRow
{
    public string species { get; set; } = "";
    public string name { get; set; } = "";
    public int level { get; set; }
    public int grade { get; set; }
    public int baseline { get; set; }
    public int surplus_threshold { get; set; }
    public int required { get; set; }
    public int usable { get; set; }
    public int protected_count { get; set; }
    public int reserved { get; set; }
    public int missing { get; set; }
    public int exhausted { get; set; }
    public int parent_reserved { get; set; }
    public int excess { get; set; }
    public bool surplus { get; set; }
}

internal static class StockPolicy
{
    internal static int Baseline(int grade) => 3 * (6 - grade);

    internal static bool CatalogValid(Snapshot s)
    {
        var rows = s.species_catalog;
        return rows != null && rows.Length > 0 && rows.Length <= 1000
            && rows.All(r => r != null && !string.IsNullOrWhiteSpace(r.species) && r.level is >= 1 and <= 5)
            && rows.Select(r => r.species).Distinct().Count() == rows.Length
            && s.fish.Where(f => !f.basic).All(f => rows.Any(r => r.species == f.species && r.level == f.level && r.seasonal == f.seasonal));
    }

    internal static bool Usable(FishState f, Snapshot s, Settings c) => !f.seasonal
        && !SeasonPolicy.HardProtected(f, c) && !SeasonPolicy.ValueProtected(f, c, s.captured_at);

    internal static IOrderedEnumerable<FishState> MaterialOrder(IEnumerable<FishState> fish) => fish
        .OrderBy(f => f.remaining_breeds).ThenBy(f => f.placed).ThenBy(f => f.id, StringComparer.Ordinal);

    // Allocation is computed in native code and recomputed immediately before consumption.
    internal static void Populate(Snapshot s, Settings c, SeasonPlan plan,
        Dictionary<(string species, int grade), int> requirements, HashSet<string> reserved, bool offseason)
    {
        var ordinary = s.fish.Where(f => Usable(f, s, c)).ToArray();
        var rows = new List<StockRow>();
        foreach (var type in s.species_catalog.Where(t => !t.seasonal).OrderBy(t => t.species, StringComparer.Ordinal))
        foreach (int grade in Enumerable.Range(1, 5))
        {
            var fish = MaterialOrder(ordinary.Where(f => f.species == type.species && f.grade == grade)).ToArray();
            int required = offseason ? Baseline(grade) : requirements.GetValueOrDefault((type.species, grade));
            if (offseason) foreach (var f in fish.Take(required)) reserved.Add(f.id);
            int held = fish.Count(f => reserved.Contains(f.id));
            rows.Add(new() { species = type.species, name = type.name, level = type.level, grade = grade,
                baseline = Baseline(grade), surplus_threshold = Baseline(grade) * 3, required = required,
                usable = fish.Length, reserved = held, missing = Math.Max(0, required - held),
                protected_count = s.fish.Count(f => f.species == type.species && f.grade == grade) - fish.Length,
                exhausted = fish.Count(f => f.remaining_breeds == 0), surplus = fish.Length >= Baseline(grade) * 3 });
        }
        // Keep two usable parents per rarity, including cooling parents. Reuse quota
        // fish when possible, then retain the greatest remaining reproductive capacity.
        var parents = new List<FishState>();
        foreach (var group in ordinary.Where(f => f.remaining_breeds > 0 && (c.protection_mode != "market"
            || f.market_state == "release" && double.IsFinite(f.market_valid_until) && f.market_valid_until > s.captured_at)).GroupBy(f => f.level))
            parents.AddRange(group.OrderByDescending(f => reserved.Contains(f.id)).ThenByDescending(f => f.remaining_breeds)
                .ThenBy(f => f.ready_at).ThenBy(f => f.id, StringComparer.Ordinal).Take(2));
        foreach (var f in parents) reserved.Add(f.id);
        var excess = ordinary.Where(f => !reserved.Contains(f.id)).ToArray();
        foreach (var row in rows)
        {
            row.parent_reserved = parents.Count(f => f.species == row.species && f.grade == row.grade);
            row.excess = excess.Count(f => f.species == row.species && f.grade == row.grade);
        }
        plan.stock = rows.ToArray();
        plan.parent_ids = parents.Select(f => f.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        plan.reserved_ids = reserved.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        plan.exhausted_ids = excess.Where(f => f.remaining_breeds == 0).Select(f => f.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var surplus = rows.Where(r => r.surplus).Select(r => (r.species, r.grade)).ToHashSet();
        plan.surplus_ids = excess.Where(f => surplus.Contains((f.species, f.grade))).Select(f => f.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (offseason) plan.needs = rows.Where(r => r.missing > 0).Select(r => new SeasonNeed { species = r.species,
            name = r.name, level = r.level, grade = r.grade, required = r.required, reserved = r.reserved, missing = r.missing }).ToArray();
    }

    internal static bool AllowsMerge(FishState[] selected, Snapshot s, Settings c, SeasonPlan plan)
    {
        if (!MergePolicy.Allows(selected)) return false;
        if (!c.auto_season_craft) return selected.All(f => f.remaining_breeds == 0);
        if (plan.policy_version != 2 || plan.state is "unavailable" or "disabled"
            || selected.Any(f => plan.reserved_ids.Contains(f.id))) return false;
        bool surplus = selected.Select(f => f.level).Distinct().Count() == 1 && selected.All(f => plan.surplus_ids.Contains(f.id));
        if (surplus)
        {
            int exhausted = s.fish.Count(f => plan.surplus_ids.Contains(f.id) && f.level == selected[0].level
                && f.grade == selected[0].grade && f.remaining_breeds == 0);
            return selected.Count(f => f.remaining_breeds == 0) >= Math.Min(10, exhausted);
        }
        if (selected.Any(f => f.remaining_breeds != 0 || !plan.exhausted_ids.Contains(f.id))) return false;
        int lower = Math.Min(5, selected.Sum(f => f.level) / 10 + 1);
        int upper = Math.Min(5, (selected.Sum(f => f.level) + 9) / 10 + 1);
        return plan.needs.Any(n => n.missing > 0 && (n.level == lower || n.level == upper));
    }
}
