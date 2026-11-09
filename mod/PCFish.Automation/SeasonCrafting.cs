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
    public int grade { get; set; }
    public int required { get; set; }
    public int reserved { get; set; }
    public int missing { get; set; }
}

internal sealed class SeasonPlan
{
    public string state { get; set; } = "disabled";
    public string reason { get; set; } = "Season crafting disabled";
    public string[] reserved_ids { get; set; } = [];
    public SeasonNeed[] needs { get; set; } = [];
    public SeasonCraft next_craft { get; set; } = null!;
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
        var season = s.season;
        SeasonPlan Unavailable(string reason) => new() { state = "unavailable", reason = reason };
        if (season == null || season.error.Length != 0 || season.id <= 0
            || !double.IsFinite(season.starts_at) || !double.IsFinite(season.ends_at)
            || season.starts_at <= 0 || season.ends_at <= season.starts_at)
            return Unavailable("Current game season/recipes unavailable; materials remain protected");
        if (!season.active || s.captured_at < season.starts_at || s.captured_at >= season.ends_at)
            return new() { state = "inactive", reason = "Outside the current game season" };
        var recipes = season.recipes;
        if (recipes == null || recipes.Length == 0 || recipes.Length > 100
            || recipes.Any(r => r == null || r.season != season.id || string.IsNullOrWhiteSpace(r.species)
                || r.grade is < 1 or > 5 || r.ingredients == null || r.ingredients.Length == 0
                || r.ingredients.Any(i => i == null || string.IsNullOrWhiteSpace(i.species) || i.grade is < 1 or > 5 || i.amount is < 1 or > 100)
                || r.ingredients.Sum(i => i.amount) > 100
                || r.ingredients.Select(i => (i.species, i.grade)).Distinct().Count() != r.ingredients.Length)
            || recipes.Select(r => (r.species, r.grade)).Distinct().Count() != recipes.Length)
            return Unavailable("Malformed current-season recipe table");
        var byKey = recipes.ToDictionary(r => (r.species, r.grade));
        if (recipes.Any(r => r.ingredients.Any(i => i.seasonal
                ? !byKey.ContainsKey((i.species, i.grade)) || i.grade >= r.grade
                : recipes.Any(output => output.species == i.species)))
            || recipes.Select(r => r.species).Distinct().Any(type => !byKey.ContainsKey((type, c.season_target_grade))))
            return Unavailable("Incomplete or cyclic season recipe chain");

        // Reserve only the quantities needed for the remaining goal. Exhausted
        // fish are used first, then fewer remaining breeds; placed fish last.
        var available = s.fish.Where(f => !HardProtected(f, c)
                && (f.seasonal ? byKey.ContainsKey((f.species, f.grade)) : f.market_state != "keep" || c.protection_mode != "market"
                    || c.UsesProductMergeValue && !ValueProtected(f, c, s.captured_at)))
            .OrderBy(f => ValueProtected(f, c, s.captured_at) && !f.seasonal)
            .ThenBy(f => f.remaining_breeds).ThenBy(f => f.placed).ThenBy(f => f.id, StringComparer.Ordinal).ToList();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var keptGoals = new HashSet<string>(StringComparer.Ordinal);
        var normalNeeds = new Dictionary<(string species, int grade), int>();
        var crafts = new Dictionary<(string species, int grade), int>();
        var goals = recipes.Where(r => r.grade == c.season_target_grade || c.season_goal == "collection" && r.grade < c.season_target_grade)
            .OrderByDescending(r => r.grade).ThenBy(r => r.species, StringComparer.Ordinal).ToArray();
        var missingGoals = new List<(SeasonRecipe recipe, int count)>();
        foreach (var goal in goals)
        {
            var owned = s.fish.Where(f => f.species == goal.species && f.grade == goal.grade).OrderBy(f => f.id, StringComparer.Ordinal).Take(c.season_target_count).ToArray();
            if (c.season_goal != "repeat")
            {
                foreach (var f in owned) { available.RemoveAll(x => x.id == f.id); reserved.Add(f.id); keptGoals.Add(f.id); }
            }
            int count = c.season_goal == "repeat" ? c.season_target_count : Math.Max(0, c.season_target_count - owned.Length);
            if (count > 0) missingGoals.Add((goal, count));
        }
        void Require((string species, int grade) key, int count, bool root = false)
        {
            if (!root)
            {
                var use = available.Where(f => f.species == key.species && f.grade == key.grade).Take(count).ToArray();
                foreach (var f in use) { reserved.Add(f.id); available.Remove(f); }
                if (!byKey.ContainsKey(key)) normalNeeds[key] = normalNeeds.GetValueOrDefault(key) + count;
                count -= use.Length;
            }
            if (count == 0 || !byKey.TryGetValue(key, out var recipe)) return;
            crafts[key] = crafts.GetValueOrDefault(key) + count;
            foreach (var i in recipe.ingredients) Require((i.species, i.grade), checked(i.amount * count));
        }
        foreach (var (recipe, count) in missingGoals) Require((recipe.species, recipe.grade), count, true);
        var ingredientNames = new Dictionary<string, string>();
        foreach (var recipe in recipes)
            foreach (var ingredient in recipe.ingredients) ingredientNames[ingredient.species] = ingredient.name;
        var plan = new SeasonPlan
        {
            state = missingGoals.Count == 0 ? "complete" : "waiting",
            reason = missingGoals.Count == 0 ? "Season collection goal reached" : "Saving exact recipe quantities; waiting for usable ingredients",
            reserved_ids = reserved.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            needs = normalNeeds.OrderBy(k => k.Key.species, StringComparer.Ordinal).ThenBy(k => k.Key.grade).Select(k =>
            {
                int held = s.fish.Count(f => reserved.Contains(f.id) && f.species == k.Key.species && f.grade == k.Key.grade);
                return new SeasonNeed { species = k.Key.species, name = ingredientNames[k.Key.species],
                    grade = k.Key.grade, required = k.Value, reserved = held, missing = Math.Max(0, k.Value - held) };
            }).ToArray()
        };
        foreach (var key in crafts.Keys.OrderByDescending(k => k.grade).ThenBy(k => k.species, StringComparer.Ordinal))
        {
            var recipe = byKey[key];
            var selected = new List<string>();
            foreach (var i in recipe.ingredients)
            {
                var use = s.fish.Where(f => reserved.Contains(f.id) && !keptGoals.Contains(f.id) && f.species == i.species && f.grade == i.grade
                        && f.seasonal == i.seasonal && !HardProtected(f, c) && (i.seasonal || !ValueProtected(f, c, s.captured_at)))
                    .OrderBy(f => f.remaining_breeds).ThenBy(f => f.placed).ThenBy(f => f.id, StringComparer.Ordinal).Take(i.amount).ToArray();
                if (use.Length != i.amount) break;
                selected.AddRange(use.Select(f => f.id));
            }
            if (selected.Count != recipe.ingredients.Sum(i => i.amount)) continue;
            plan.next_craft = new() { season = season.id, species = recipe.species, grade = recipe.grade, fish_ids = selected.ToArray() };
            plan.state = "ready"; plan.reason = "Exact current-season recipe materials ready";
            break;
        }
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
