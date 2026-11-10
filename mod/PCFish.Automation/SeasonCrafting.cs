namespace PCFish.Automation;

internal sealed class SeasonIngredient
{
    public string species { get; set; } = "";
    public string name { get; set; } = "";
    public int grade { get; set; }
    public int amount { get; set; }
    public bool seasonal { get; set; }
}

internal sealed class SeasonRecipe
{
    public int season { get; set; }
    public string species { get; set; } = "";
    public string name { get; set; } = "";
    public int grade { get; set; }
    public SeasonIngredient[] ingredients { get; set; } = [];
}

internal sealed class SeasonState
{
    public int id { get; set; }
    public bool active { get; set; }
    public double starts_at { get; set; }
    public double ends_at { get; set; }
    public string error { get; set; } = "";
    public SeasonRecipe[] recipes { get; set; } = [];
    public SeasonPlan plan { get; set; } = new();
}

internal sealed class SeasonCraft
{
    public int season { get; set; }
    public string species { get; set; } = "";
    public int grade { get; set; }
    public string[] fish_ids { get; set; } = [];
}

internal sealed class SeasonNeed
{
    public string species { get; set; } = "";
    public string name { get; set; } = "";
    public int level { get; set; }
    public int grade { get; set; }
    public int required { get; set; }
    public int reserved { get; set; }
    public int missing { get; set; }
}

internal sealed class SeasonPlan
{
    public int policy_version { get; set; } = 2;
    public string phase { get; set; } = "disabled";
    public int focus_grade { get; set; }
    public StockRow[] stock { get; set; } = [];
    public string[] parent_ids { get; set; } = [];
    public string[] exhausted_ids { get; set; } = [];
    public string[] surplus_ids { get; set; } = [];
    public string state { get; set; } = "disabled";
    public string reason { get; set; } = "Season crafting disabled";
    public string[] reserved_ids { get; set; } = [];
    public SeasonNeed[] needs { get; set; } = [];
    public SeasonCraft next_craft { get; set; } = null!;
    public string[] species_priority { get; set; } = [];
    public string focus_species { get; set; } = "";
    public string focus_name { get; set; } = "";
}

// One authoritative allocator serves both exported recommendations and the
// execution guard. Never accept Python's material reservation or recipe claims.
internal static class SeasonPolicy
{
    internal static bool HardProtected(FishState f, Settings c) => f.basic || f.locked
        || c.reserved_ids.Contains(f.id) || c.reserved_species.Contains(f.species);

    internal static bool ValueProtected(FishState f, Settings c, double now) => c.protection_mode == "market"
        ? !f.basic && (c.UsesProductMergeValue
            ? f.market_merge_state != "release" || !double.IsFinite(f.market_merge_valid_until) || f.market_merge_valid_until <= now
            : f.market_state != "release" || !double.IsFinite(f.market_valid_until) || f.market_valid_until <= now)
        : f.grade >= c.protect_grade_at_least;

    internal static SeasonPlan Build(Snapshot s, Settings c)
    {
        if (!c.auto_season_craft) return new();
        try { return BuildEnabled(s, c); }
        catch (OverflowException) { return new() { state = "unavailable", reason = "Season recipe quantity overflow" }; }
    }

    static SeasonPlan BuildEnabled(Snapshot s, Settings c)
    {
        SeasonPlan Unavailable(string reason) => new() { state = "unavailable", reason = reason };
        if (!StockPolicy.CatalogValid(s)) return Unavailable("Authoritative species catalog unavailable or inconsistent");
        var catalog = new Dictionary<string, SpeciesInfo>(StringComparer.Ordinal);
        foreach (var type in s.species_catalog) catalog.Add(type.species, type);
        var season = s.season;
        if (season == null || season.error.Length != 0 || season.id <= 0
            || !double.IsFinite(season.starts_at) || !double.IsFinite(season.ends_at)
            || season.starts_at <= 0 || season.ends_at <= season.starts_at)
            return Unavailable("Current game season unavailable; materials remain protected");
        if (!season.active || s.captured_at < season.starts_at || s.captured_at >= season.ends_at)
        {
            var inactive = new SeasonPlan { state = "inactive", phase = "stockpile", reason = "Stockpile every ordinary species and star grade" };
            StockPolicy.Populate(s, c, inactive, new(), new(StringComparer.Ordinal), true);
            return inactive;
        }
        var recipes = season.recipes;
        if (recipes == null || recipes.Length == 0 || recipes.Length > 100
            || recipes.Any(r => r == null || r.season != season.id || string.IsNullOrWhiteSpace(r.species) || !catalog.TryGetValue(r.species, out var type) || !type.seasonal
                || r.grade is < 1 or > 5 || r.ingredients == null || r.ingredients.Length == 0
                || r.ingredients.Any(i => i == null || string.IsNullOrWhiteSpace(i.species) || !catalog.TryGetValue(i.species, out var material) || material.seasonal != i.seasonal
                    || i.grade is < 1 or > 5 || i.amount is < 1 or > 100)
                || r.ingredients.Sum(i => i.amount) > 100
                || r.ingredients.Select(i => (i.species, i.grade)).Distinct().Count() != r.ingredients.Length)
            || recipes.Select(r => (r.species, r.grade)).Distinct().Count() != recipes.Length)
            return Unavailable("Malformed current-season recipe table");
        var byKey = recipes.ToDictionary(r => (r.species, r.grade));
        var types = recipes.Select(r => r.species).Distinct().ToArray();
        if (recipes.Any(r => r.ingredients.Any(i => i.seasonal
                && (i.species != r.species || !byKey.ContainsKey((i.species, i.grade)) || i.grade >= r.grade)))
            || types.Any(type => Enumerable.Range(1, c.season_target_grade).Any(g => !byKey.ContainsKey((type, g)))))
            return Unavailable("Incomplete or cyclic season recipe chain");
        int Priority(string species)
        {
            int index = Array.IndexOf(c.season_species_priority, species);
            return index < 0 ? int.MaxValue : index;
        }
        var order = types.OrderByDescending(type => catalog[type].level).ThenBy(Priority).ThenBy(type => type, StringComparer.Ordinal).ToArray();
        var available = StockPolicy.MaterialOrder(s.fish.Where(f => !HardProtected(f, c)
            && (f.seasonal ? byKey.ContainsKey((f.species, f.grade)) : !ValueProtected(f, c, s.captured_at)))).ToList();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var allocated = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var focusReserved = new HashSet<string>(StringComparer.Ordinal);
        var allNeeds = new Dictionary<(string species, int grade), int>();
        var focusNeeds = new Dictionary<(string species, int grade), int>();
        var focusCrafts = new HashSet<(string species, int grade)>();
        string focus = ""; int focusGrade = 0;
        void Require((string species, int grade) key, int count, bool trace, bool root = false)
        {
            if (count == 0) return;
            if (count > 1000000) throw new OverflowException();
            if (!root)
            {
                if (!byKey.ContainsKey(key))
                {
                    allNeeds[key] = checked(allNeeds.GetValueOrDefault(key) + count);
                    if (trace) focusNeeds[key] = checked(focusNeeds.GetValueOrDefault(key) + count);
                }
                var use = available.Where(f => f.species == key.species && f.grade == key.grade).Take(count).ToArray();
                foreach (var f in use)
                {
                    reserved.Add(f.id); allocated.Add(f.id); available.Remove(f);
                    if (trace) focusReserved.Add(f.id);
                }
                count -= use.Length;
            }
            if (count == 0 || !byKey.TryGetValue(key, out var recipe)) return;
            if (trace) focusCrafts.Add(key);
            foreach (var i in recipe.ingredients) Require((i.species, i.grade), checked(i.amount * count), trace);
        }
        void KeepGoal(string type, int grade, out int missing)
        {
            // Locked goals count as owned, but locked intermediate fish cannot be consumed.
            var owned = s.fish.Where(f => f.species == type && f.grade == grade && !allocated.Contains(f.id))
                .OrderByDescending(f => HardProtected(f, c)).ThenBy(f => f.id, StringComparer.Ordinal).Take(c.season_target_count).ToArray();
            foreach (var f in owned) { kept.Add(f.id); reserved.Add(f.id); allocated.Add(f.id); available.RemoveAll(a => a.id == f.id); }
            missing = c.season_target_count - owned.Length;
        }
        if (c.season_goal != "repeat")
        {
            foreach (var type in order)
            foreach (int grade in (c.season_goal == "collection" ? Enumerable.Range(1, c.season_target_grade).Reverse() : new[] { c.season_target_grade }))
            {
                KeepGoal(type, grade, out int missing);
                if (missing == 0) continue;
                bool trace = focus.Length == 0;
                if (trace) { focus = type; focusGrade = grade; }
                // Allocate higher goals first. A last lower-grade fish can be assigned
                // here and no longer counts as a retained lower-grade collection goal.
                Require((type, grade), missing, trace, true);
            }
        }
        else
        {
            foreach (var type in order)
            foreach (int grade in Enumerable.Range(1, c.season_target_grade)) KeepGoal(type, grade, out _);
        }
        bool repeating = c.season_goal == "repeat" || c.season_goal == "collection" && focus.Length == 0;
        if (repeating)
        {
            focus = order[0]; focusGrade = c.season_target_grade;
            Require((focus, focusGrade), c.season_target_count, true, true);
        }
        var plan = new SeasonPlan { state = focus.Length == 0 ? "complete" : "waiting", phase = repeating ? "repeat" : "collection",
            reason = focus.Length == 0 ? "Season highest-grade goals reached" : "Acquire materials for the highest missing star grade",
            species_priority = order, focus_species = focus, focus_grade = focusGrade,
            focus_name = focus.Length == 0 ? "" : catalog[focus].name,
            needs = focusNeeds.OrderBy(k => k.Key.species, StringComparer.Ordinal).ThenBy(k => k.Key.grade).Select(k =>
            {
                int held = s.fish.Count(f => focusReserved.Contains(f.id) && f.species == k.Key.species && f.grade == k.Key.grade);
                return new SeasonNeed { species = k.Key.species, name = catalog[k.Key.species].name, level = catalog[k.Key.species].level,
                    grade = k.Key.grade, required = k.Value, reserved = held, missing = Math.Max(0, k.Value - held) };
            }).ToArray() };
        foreach (var key in focusCrafts.OrderByDescending(k => k.grade).ThenBy(k => k.species, StringComparer.Ordinal))
        {
            var recipe = byKey[key];
            var ids = new List<string>();
            foreach (var i in recipe.ingredients)
            {
                var use = StockPolicy.MaterialOrder(s.fish.Where(f => focusReserved.Contains(f.id) && !kept.Contains(f.id)
                    && f.species == i.species && f.grade == i.grade && f.seasonal == i.seasonal
                    && !HardProtected(f, c) && (i.seasonal || !ValueProtected(f, c, s.captured_at)))).Take(i.amount).ToArray();
                if (use.Length != i.amount) break;
                ids.AddRange(use.Select(f => f.id));
            }
            if (ids.Count != recipe.ingredients.Sum(i => i.amount)) continue;
            plan.next_craft = new() { season = season.id, species = recipe.species, grade = recipe.grade, fish_ids = ids.ToArray() };
            plan.state = "ready"; plan.reason = "Highest-priority season upgrade materials ready";
            break;
        }
        StockPolicy.Populate(s, c, plan, allNeeds, reserved, false);
        return plan;
    }

    internal static void ValidateCraft(Command cmd, Snapshot s, Settings c, SeasonPlan plan)
    {
        var next = plan.next_craft;
        if (!c.auto_season_craft || next == null || cmd.season != next.season || cmd.target_species != next.species
            || cmd.target_grade != next.grade || !cmd.fish_ids.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(next.fish_ids.OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidDataException("Season goal, recipe or protected materials changed");
    }

    internal static void Confirm(Command cmd, Snapshot before, Snapshot after, FishState child)
    {
        if (child.species != cmd.target_species || child.grade != cmd.target_grade || !child.seasonal
            || after.hearts < before.hearts || after.fish.Length != before.fish.Length - cmd.fish_ids.Length + 1
            || cmd.fish_ids.Any(id => after.fish.Any(f => f.id == id))
            || before.fish.Where(f => !cmd.fish_ids.Contains(f.id)).Any(f => !after.fish.Any(a => a.id == f.id)))
            throw new InvalidDataException("Season craft result did not match exact recipe consumption/output");
    }
}
