using PCFish.Automation;

internal static class SeasonStockTests
{
    internal static Settings Config() => new() { enabled = true, mode = "protected", auto_season_craft = true,
        season_goal = "collection", protection_mode = "market", market_merge_price_basis = "highest_buy_order" };
    internal static FishState Fish(string id, string species, int level, int grade = 1, int breeds = 0, bool seasonal = false) => new()
    {
        id = id, species = species, level = level, grade = grade, remaining_breeds = breeds, ready = breeds > 0,
        seasonal = seasonal, market_state = "release", market_merge_state = "release", market_valid_until = 9999, market_merge_valid_until = 9999
    };
    internal static Snapshot State()
    {
        var catalog = new List<SpeciesInfo>(); var recipes = new List<SeasonRecipe>();
        foreach (int rarity in new[] { 5, 4, 3 })
        {
            string type = "S" + rarity;
            catalog.Add(new() { species = type, name = type, level = rarity, seasonal = true });
            catalog.Add(new() { species = type + "seed", name = type + "seed", level = rarity - 1 });
            foreach (string suffix in new[] { "a", "b", "c" }) catalog.Add(new() { species = type + suffix, name = type + suffix, level = rarity - 2 });
            foreach (int grade in Enumerable.Range(1, 5)) recipes.Add(new() { season = 2, species = type, name = type, grade = grade,
                ingredients = new[] { new SeasonIngredient { species = grade == 1 ? type + "seed" : type, grade = Math.Max(1, grade - 1), amount = 1, seasonal = grade > 1 } }
                    .Concat(new[] { "a", "b", "c" }.Select(suffix => new SeasonIngredient { species = type + suffix, grade = grade, amount = 3 })).ToArray() });
        }
        return new() { captured_at = 1000, capacity = 999, hearts = 1, species_catalog = catalog.ToArray(),
            season = new() { id = 2, active = true, starts_at = 100, ends_at = 9999, recipes = recipes.ToArray() } };
    }
    internal static Snapshot Offseason(int count, int grade = 1, int breeds = 0)
    {
        var s = State(); s.season.active = false;
        s.species_catalog = [new() { species = "A", name = "A", level = 1 }, new() { species = "B", name = "B", level = 1 }];
        s.fish = Enumerable.Range(0, count).Select(i => Fish("a" + i, "A", 1, grade, breeds)).ToArray();
        return s;
    }
    static Command Craft(SeasonPlan p) => new() { request_id = Guid.NewGuid().ToString(), kind = "craft", season = p.next_craft.season,
        target_species = p.next_craft.species, target_grade = p.next_craft.grade, fish_ids = p.next_craft.fish_ids };
    static void AddIngredients(Snapshot s, string type, int fromGrade, int throughGrade, int each = 3)
    {
        int level = s.species_catalog.Single(t => t.species == type + "a").level;
        s.fish = s.fish.Concat(Enumerable.Range(fromGrade, throughGrade - fromGrade + 1).SelectMany(g => new[] { "a", "b", "c" }
            .SelectMany(suffix => Enumerable.Range(0, each).Select(i => Fish($"{type}{suffix}-{g}-{i}", type + suffix, level, g))))).ToArray();
    }
    internal static void Run(Action<bool, string> check)
    {
        var c = Config(); var s = State();
        s.fish = [Fish("only-two", "S5", 5, 2, seasonal: true)]; AddIngredients(s, "S5", 3, 5);
        var p = SeasonPolicy.Build(s, c);
        check(p.focus_species == "S5" && p.focus_grade == 5 && p.next_craft?.grade == 3, "highest rarity and highest missing grade first");
        check(p.next_craft!.fish_ids.Contains("only-two"), "last two-star upgrades; no collection lock before first five-star");
        check(p.needs.All(n => n.grade >= 3) && p.needs.All(n => n.missing == 0), "existing two-star removes lower-stage focused needs");
        check(p.stock.Any(r => r.species == "S5a" && r.grade == 1 && r.required > 0), "later collection needs remain separately protected");
        ExecutionGuard.Validate(Craft(p), s, c); check(true, "native guard accepts focused two-to-three craft");
        s.fish.Single(f => f.id == "only-two").locked = true; p = SeasonPolicy.Build(s, c);
        check(p.next_craft == null && p.needs.Any(n => n.grade == 1), "manual lock requires another chain and is not bypassed");

        s = State(); s.fish = [Fish("five", "S5", 5, 5, seasonal: true), Fish("two", "S5", 5, 2, seasonal: true)];
        AddIngredients(s, "S5", 3, 4); p = SeasonPolicy.Build(s, c);
        check(p.focus_grade == 4 && p.next_craft?.grade == 3 && p.reserved_ids.Contains("five"), "keep completed five-star while building missing four-star");
        c.season_species_priority = ["S3", "S4", "S5"]; p = SeasonPolicy.Build(s, c);
        check(p.focus_species == "S5", "catalog rarity overrides configured order across rarities");

        s = State();
        foreach (var type in new[] { "S5", "S4", "S3" })
        {
            int level = s.species_catalog.Single(t => t.species == type + "seed").level;
            s.fish = s.fish.Concat(Enumerable.Range(0, 5).Select(i => Fish(type + "seed" + i, type + "seed", level))).ToArray();
            foreach (int grade in Enumerable.Range(1, 5)) AddIngredients(s, type, grade, grade, 3 * (6 - grade));
        }
        var completed = new List<(string, int)>();
        for (int i = 0; i < 45; i++)
        {
            p = SeasonPolicy.Build(s, c); check(p.state == "ready", "full stock has a ready highest-first craft " + i);
            var cmd = Craft(p); ExecutionGuard.Validate(cmd, s, c);
            var child = Fish("crafted" + i, cmd.target_species, s.species_catalog.Single(t => t.species == cmd.target_species).level,
                cmd.target_grade, seasonal: true);
            s.fish = s.fish.Where(f => !cmd.fish_ids.Contains(f.id)).Append(child).ToArray();
            completed.Add((cmd.target_species, cmd.target_grade));
        }
        check(completed.Take(5).Select(k => k.Item2).SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "first chain goes straight to five-star");
        check(completed.Take(15).All(k => k.Item1 == "S5") && completed.Skip(15).Take(15).All(k => k.Item1 == "S4"), "finish each species before lower rarity");
        check(s.fish.Length == 15 && s.fish.GroupBy(f => (f.species, f.grade)).All(g => g.Count() == 1), "full pyramid leaves all fifteen collection fish");
        p = SeasonPolicy.Build(s, c);
        check(p.phase == "repeat" && p.focus_species == "S5" && p.focus_grade == 5, "collection automatically repeats highest rarity five-star");
        check(s.fish.All(f => p.reserved_ids.Contains(f.id)), "repeat never consumes completed collection");

        s = Offseason(45); p = SeasonPolicy.Build(s, c);
        check(p.stock.Where(r => r.species == "A").Select(r => r.baseline).SequenceEqual(new[] { 15, 12, 9, 6, 3 }), "every grade has pyramid baseline");
        check(p.stock.Where(r => r.species == "B").All(r => r.missing == r.baseline), "unowned species included in offseason stock");
        check(p.surplus_ids.Length == 30 && p.reserved_ids.Length == 15, "triple threshold releases only stock excess");
        var batch = s.fish.Where(f => p.surplus_ids.Contains(f.id)).Take(10).ToArray();
        check(StockPolicy.AllowsMerge(batch, s, c, p), "surplus same-rarity merge allowed");
        s.fish = s.fish.Where(f => !batch.Contains(f)).ToArray(); p = SeasonPolicy.Build(s, c);
        check(p.surplus_ids.Length == 0, "surplus exception closes below triple after one batch");
        s = Offseason(30); s.fish = s.fish.Concat(Enumerable.Range(0, 30).Select(i => Fish("b" + i, "B", 1))).ToArray();
        p = SeasonPolicy.Build(s, c); check(p.surplus_ids.Length == 0, "different species cannot combine to reach threshold");
        s = Offseason(9, 5); p = SeasonPolicy.Build(s, c);
        check(p.surplus_ids.Length == 6 && !StockPolicy.AllowsMerge(s.fish, s, c, p), "nine five-stars do not make ten excess materials");
        s = Offseason(13, 5); p = SeasonPolicy.Build(s, c);
        check(StockPolicy.AllowsMerge(s.fish.Where(f => p.surplus_ids.Contains(f.id)).ToArray(), s, c, p), "thirteen five-stars leave three and merge ten");

        s = Offseason(45, breeds: 3); foreach (var f in s.fish.Take(25)) f.remaining_breeds = 0;
        p = SeasonPolicy.Build(s, c);
        check(p.parent_ids.Length == 2 && p.parent_ids.All(p.reserved_ids.Contains), "retain two breeding parents per rarity");
        var excess = s.fish.Where(f => p.surplus_ids.Contains(f.id)).ToArray();
        batch = excess.OrderBy(f => f.remaining_breeds).Take(10).ToArray();
        check(StockPolicy.AllowsMerge(batch, s, c, p), "exhausted surplus used before positive-breed fish");
        batch = excess.OrderByDescending(f => f.remaining_breeds).Take(10).ToArray();
        check(!StockPolicy.AllowsMerge(batch, s, c, p), "cannot skip available exhausted surplus");
        foreach (var f in s.fish) f.locked = true; p = SeasonPolicy.Build(s, c);
        check(p.surplus_ids.Length == 0 && p.stock.Single(r => r.species == "A" && r.grade == 1).usable == 0, "locked inventory cannot inflate surplus");
        s = Offseason(45); s.species_catalog = []; p = SeasonPolicy.Build(s, c);
        check(p.state == "unavailable", "missing catalog fails closed");
        s = Offseason(45); s.season.error = "unknown";
        check(SeasonPolicy.Build(s, c).state == "unavailable", "unknown season is not offseason");

        s = Offseason(30); s.fish = s.fish.Concat(Enumerable.Range(0, 15).Select(i => Fish("grade2-" + i, "A", 1, 2))).ToArray();
        check(SeasonPolicy.Build(s, c).surplus_ids.Length == 0, "different grades cannot combine to reach triple threshold");
        s = State(); s.species_catalog = s.species_catalog.Append(new() { species = "extra", name = "extra", level = 2 }).ToArray();
        s.fish = Enumerable.Range(0, 10).Select(i => Fish("extra" + i, "extra", 2)).ToArray(); p = SeasonPolicy.Build(s, c);
        check(StockPolicy.AllowsMerge(s.fish, s, c, p), "exhausted unreserved material may target missing rarity below surplus threshold");
        foreach (var f in s.fish) f.remaining_breeds = 1; p = SeasonPolicy.Build(s, c);
        check(!StockPolicy.AllowsMerge(s.fish, s, c, p), "targeted merge does not bypass remaining-use restriction");

        s = Offseason(45, breeds: 3); p = SeasonPolicy.Build(s, c);
        batch = s.fish.Where(f => p.surplus_ids.Contains(f.id)).Take(10).ToArray();
        var mergeCommand = new Command { request_id = Guid.NewGuid().ToString(), kind = "merge", fish_ids = batch.Select(f => f.id).ToArray() };
        ExecutionGuard.Validate(mergeCommand, s, c); check(true, "positive-breed excess executes only after quota and parent allocation");
        var remove = s.fish.First(f => !batch.Contains(f)); s.fish = s.fish.Where(f => f != remove).ToArray();
        bool rejected = false; try { ExecutionGuard.Validate(mergeCommand, s, c); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "native guard recomputes threshold after inventory changes");

        s = Offseason(45, breeds: 3); s.species_catalog = s.species_catalog.Append(new() { species = "C", level = 2 }).ToArray();
        s.fish = s.fish.Concat(Enumerable.Range(0, 45).Select(i => Fish("c" + i, "C", 2, breeds: 3))).ToArray(); p = SeasonPolicy.Build(s, c);
        batch = s.fish.Where(f => p.surplus_ids.Contains(f.id) && f.level == 1).Take(5)
            .Concat(s.fish.Where(f => p.surplus_ids.Contains(f.id) && f.level == 2).Take(5)).ToArray();
        check(!StockPolicy.AllowsMerge(batch, s, c, p), "positive-breed overflow cannot mix adjacent rarities");
    }
}
