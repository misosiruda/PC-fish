using PCFish.Automation;

internal static class SeasonTests
{
    internal static void Run(Action<bool, string> check)
    {
        Settings Config() => new() { enabled = true, mode = "protected", auto_season_craft = true,
            season_target_grade = 2, protection_mode = "market" };
        FishState Fish(string id, string species, int grade = 1, bool seasonal = false) => new()
        { id = id, species = species, grade = grade, level = 2, seasonal = seasonal, ready = true,
            remaining_breeds = 0, market_state = "release", market_valid_until = 9999 };
        SeasonRecipe[] Recipes() => [
            new() { season = 2, species = "S", grade = 1, ingredients = [
                new() { species = "A", grade = 1, amount = 1 }, new() { species = "B", grade = 1, amount = 2 }] },
            new() { season = 2, species = "S", grade = 2, ingredients = [
                new() { species = "S", grade = 1, amount = 1, seasonal = true }, new() { species = "B", grade = 2, amount = 2 }] }
        ];
        Snapshot State() => new() { captured_at = 1000, capacity = 100, hearts = 1, season = new()
        { id = 2, active = true, starts_at = 100, ends_at = 10000, recipes = Recipes() },
            fish = [Fish("a", "A"), Fish("b1", "B"), Fish("b2", "B"), Fish("b3", "B"), Fish("b4", "B", 2), Fish("b5", "B", 2)] };
        Command Craft(SeasonPlan plan) => new() { request_id = Guid.NewGuid().ToString(), kind = "craft",
            season = plan.next_craft!.season, target_species = plan.next_craft.species,
            target_grade = plan.next_craft.grade, fish_ids = plan.next_craft.fish_ids };
        void Rejected(Command cmd, Snapshot state, Settings cfg, string label)
        {
            bool rejected = false;
            try { ExecutionGuard.Validate(cmd, state, cfg); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { rejected = true; }
            check(rejected, label);
        }
        var cfg = Config(); var s = State(); var plan = SeasonPolicy.Build(s, cfg);
        check(plan.state == "ready" && plan.next_craft!.grade == 1, "first missing stage crafts before higher stage");
        check(plan.reserved_ids.Length == 5 && !plan.reserved_ids.Contains("b3"), "reserve exact entire-chain quantity and release surplus");
        check(plan.needs.Sum(n => n.required) == 5 && plan.needs.All(n => n.missing == 0), "report complete-chain material requirements");
        var cmd = Craft(plan);
        ExecutionGuard.Validate(cmd, s, cfg); check(true, "craft is allowed despite recipe reservation");
        var breed = new Command { request_id = Guid.NewGuid().ToString(), kind = "breed", fish_ids = ["a", "b1"] };
        foreach (var f in s.fish) f.remaining_breeds = 1;
        plan = SeasonPolicy.Build(s, cfg);
        check(breed.fish_ids.All(plan.reserved_ids.Contains), "both test parents are really season-reserved materials");
        ExecutionGuard.Validate(breed, s, cfg); check(true, "reserved ordinary materials may breed without losing recipe quantity");
        foreach (var id in breed.fish_ids) s.fish.Single(f => f.id == id).remaining_breeds--;
        plan = SeasonPolicy.Build(s, cfg);
        ExecutionGuard.Validate(Craft(plan), s, cfg);
        check(plan.needs.All(n => n.missing == 0), "exhausted reserved parents remain usable for the exact season recipe");
        foreach (var protection in new[] { "locked", "manual_id", "manual_species", "keep", "pending", "expired", "seasonal", "disabled" })
        {
            var protectedState = State(); var protectedCfg = Config();
            foreach (var f in protectedState.fish) f.remaining_breeds = 1;
            switch (protection)
            {
                case "locked": protectedState.fish[0].locked = true; break;
                case "manual_id": protectedCfg.reserved_ids = ["a"]; break;
                case "manual_species": protectedCfg.reserved_species = ["A"]; break;
                case "keep": protectedState.fish[0].market_state = "keep"; break;
                case "pending": protectedState.fish[0].market_state = "pending"; break;
                case "expired": protectedState.fish[0].market_valid_until = protectedState.captured_at; break;
                case "seasonal": protectedState.fish[0].seasonal = true; break;
                case "disabled": protectedCfg.auto_breed = false; break;
            }
            Rejected(breed, protectedState, protectedCfg, "season reservation never bypasses breed protection: " + protection);
        }
        var mergeState = State(); mergeState.fish = Enumerable.Range(0, 10).Select(i => Fish("a" + i, "A")).ToArray();
        Rejected(new() { request_id = Guid.NewGuid().ToString(), kind = "merge", fish_ids = mergeState.fish.Select(f => f.id).ToArray() }, mergeState, cfg,
            "forged ordinary merge cannot consume seasonal quota");
        foreach (var change in new[] { "paused", "observe", "disabled", "busy", "pending", "uncertain", "locked", "manual", "market_keep", "market_pending", "expired", "grade", "season", "output", "count", "wrong_material", "duplicate", "end", "before_start", "recipe_changed", "missing_recipe", "malformed", "cycle" })
        {
            s = State(); cfg = Config(); cmd = Craft(SeasonPolicy.Build(s, cfg));
            switch (change)
            {
                case "paused": cfg.enabled = false; break;
                case "observe": cfg.mode = "observe"; break;
                case "disabled": cfg.auto_season_craft = false; break;
                case "busy": s.ui_busy = true; break;
                case "pending": s.request_in_flight = true; break;
                case "uncertain": s.uncertain = true; break;
                case "locked": s.fish[0].locked = true; break;
                case "manual": cfg.reserved_ids = ["a"]; break;
                case "market_keep": s.fish[0].market_state = "keep"; break;
                case "market_pending": s.fish[0].market_state = "pending"; break;
                case "expired": s.fish[0].market_valid_until = 1000; break;
                case "grade": cfg.protection_mode = "grade"; cfg.protect_grade_at_least = 2; s.fish[0].grade = 2; break;
                case "season": cmd.season = 1; break;
                case "output": cmd.target_species = "wrong"; break;
                case "count": cmd.fish_ids = ["a"]; break;
                case "wrong_material": cmd.fish_ids = ["a", "b1", "b4"]; break;
                case "duplicate": cmd.fish_ids = ["a", "b1", "b1"]; break;
                case "end": s.captured_at = 10000; break;
                case "before_start": s.captured_at = 99; break;
                case "recipe_changed": s.season!.recipes[0].ingredients[0].amount = 2; break;
                case "missing_recipe": s.season!.recipes = []; break;
                case "malformed": s.season!.recipes[0].ingredients[0].amount = 0; break;
                case "cycle": s.season!.recipes[0].ingredients = [new() { species = "S", grade = 2, seasonal = true, amount = 1 }]; break;
            }
            Rejected(cmd, s, cfg, "craft rejects " + change);
        }
        s = State(); cfg = Config(); s.fish[0].market_state = "pending";
        plan = SeasonPolicy.Build(s, cfg);
        check(plan.state == "waiting" && plan.reserved_ids.Contains("a"), "unknown-price materials reserved while quote is pending");
        s = State(); s.fish[0].remaining_breeds = 3;
        s.fish = s.fish.Append(Fish("a-exhausted", "A")).ToArray();
        plan = SeasonPolicy.Build(s, cfg);
        check(plan.reserved_ids.Contains("a-exhausted") && !plan.reserved_ids.Contains("a"), "prefer exhausted materials and keep useful parents");
        s = State(); s.fish = s.fish.Where(f => f.species != "A").Append(Fish("s1", "S", 1, true)).ToArray();
        plan = SeasonPolicy.Build(s, cfg); cmd = Craft(plan);
        check(cmd.target_grade == 2 && cmd.fish_ids.Contains("s1") && plan.needs.Length == 1, "owned precursor avoids rebuilding earlier stage");
        ExecutionGuard.Validate(cmd, s, cfg); check(true, "exact seasonal precursor can upgrade despite no active-season market quote");
        s.fish.Single(f => f.id == "s1").locked = true;
        Rejected(cmd, s, cfg, "locked seasonal precursor never consumed");
        s = State(); s.fish = s.fish.Append(Fish("goal", "S", 2, true)).ToArray(); s.fish.Last().locked = true;
        plan = SeasonPolicy.Build(s, cfg);
        check(plan.state == "complete" && plan.next_craft == null && !plan.reserved_ids.Contains("a"), "owned locked final goal stops crafting and releases normal quota");
        cfg.season_goal = "repeat"; plan = SeasonPolicy.Build(s, cfg);
        check(plan.state == "ready", "repeat mode replenishes a new batch after final goal exists");
        s = State(); cfg = Config(); cfg.season_goal = "collection";
        s.fish = s.fish.Append(Fish("kept-s1", "S", 1, true)).ToArray();
        plan = SeasonPolicy.Build(s, cfg);
        check(plan.next_craft!.grade == 1 && !plan.next_craft.fish_ids.Contains("kept-s1"), "collection keeps one of each grade while building extra precursor");
        check(plan.reserved_ids.Contains("kept-s1"), "collection goal fish stay reserved");
        s = State(); cfg = Config(); cfg.season_target_count = 2;
        plan = SeasonPolicy.Build(s, cfg);
        check(plan.needs.Sum(n => n.required) == 10 && plan.needs.Sum(n => n.missing) == 4, "multi-copy quota uses inventory once without double counting");
        s = State(); cfg = Config(); s.season!.active = false;
        check(SeasonPolicy.Build(s, cfg).state == "inactive" && SeasonPolicy.Build(s, cfg).reserved_ids.Length == 0, "season end releases ordinary recipe reservations");
        s.season.error = "unknown";
        check(SeasonPolicy.Build(s, cfg).state == "unavailable", "unknown season state fails closed");
        Rejected(breed, s, cfg, "unknown table blocks regular consumption too");
        s = State(); cfg = Config(); cfg.auto_season_craft = false;
        check(SeasonPolicy.Build(s, cfg).state == "disabled", "feature switch releases automatic quota");

        s = State(); cfg = Config(); cmd = Craft(SeasonPolicy.Build(s, cfg));
        var child = Fish("new-season", "S", 1, true);
        var after = State(); after.fish = s.fish.Where(f => !cmd.fish_ids.Contains(f.id)).Append(child).ToArray();
        SeasonPolicy.Confirm(cmd, s, after, child); check(true, "confirmed craft consumes exact materials and no hearts");
        after.hearts++;
        SeasonPolicy.Confirm(cmd, s, after, child); check(true, "normal heart regeneration during craft is allowed");
        foreach (var bad in new[] { "output", "grade", "seasonal", "heart", "material_present", "unrelated_lost" })
        {
            child = Fish("new-season", "S", 1, true); after = State();
            after.fish = s.fish.Where(f => !cmd.fish_ids.Contains(f.id)).Append(child).ToArray();
            switch (bad)
            {
                case "output": child.species = "wrong"; break;
                case "grade": child.grade = 2; break;
                case "seasonal": child.seasonal = false; break;
                case "heart": after.hearts = 0; break;
                case "material_present": after.fish = after.fish.Append(s.fish[0]).ToArray(); break;
                case "unrelated_lost": after.fish = after.fish.Skip(1).ToArray(); break;
            }
            bool rejected = false;
            try { SeasonPolicy.Confirm(cmd, s, after, child); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "ambiguous craft result stops on " + bad);
        }
        foreach (var bad in new[] { "goal", "grade", "count" })
        {
            cfg = Config();
            if (bad == "goal") cfg.season_goal = "unknown";
            if (bad == "grade") cfg.season_target_grade = 6;
            if (bad == "count") cfg.season_target_count = 0;
            bool rejected = false; try { cfg.Validate(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid season setting " + bad);
        }
    }
}
