using PCFish.Automation;

internal static class LevelRewardTests
{
    internal static void Run(Action<bool, string> check)
    {
        FishState Ordinary() => new() { id = "ordinary", species = "regular", variant = "v", level = 2, grade = 1,
            remaining_breeds = 3, maximum_breeds = 4, growth = 5 };
        FishState Reward() => new() { id = "BF004", species = "FS00000", basic = true, growth = 10, placed = true };
        Snapshot Before() => new() { tank_level = 5, tank_exp = 1760, hearts = 2, max_tank_level = 10,
            max_tank_exp = 99999999, next_level_reward_fish_ids = ["BF004"], fish = [Ordinary()] };
        Snapshot After() => new() { tank_level = 6, tank_exp = 1770, hearts = 2, fish = [Ordinary(), Reward()] };

        foreach (var observation in new[] { "reward_xp", "no_xp", "feeding_xp", "growth", "extra_reward", "delayed_reward", "placement",
                     "reward_not_mature", "heart_regenerated", "missing_table", "level_advanced_twice" })
        {
            var before = Before(); var after = After();
            switch (observation)
            {
                case "no_xp": after.tank_exp = before.tank_exp; break;
                case "feeding_xp": after.tank_exp = 1780; after.fish[0].growth = 10; break;
                case "growth": after.fish[0].growth = 6; break;
                case "extra_reward": var extra = Reward(); extra.id = "other-reward"; after.fish = [Ordinary(), Reward(), extra]; break;
                case "delayed_reward": after.fish = [Ordinary()]; break;
                case "placement": after.fish[0].placed = true; break;
                case "reward_not_mature": after.fish[1].growth = 5; break;
                case "heart_regenerated": after.hearts = 3; break;
                case "missing_table": before.next_level_reward_fish_ids = null!; before.max_tank_exp = 0; break;
                case "level_advanced_twice": after.tank_level = 7; break;
            }
            check(LevelUpGuard.Confirm(before, after), "normal observation is not an error: " + observation);
        }
        var unchanged = After(); unchanged.tank_level = 5;
        check(!LevelUpGuard.Confirm(Before(), unchanged), "no progress is a deferred attempt, not an exception");
        foreach (var loss in new[] { "fish_removed", "breeding_use", "lock_removed", "heart_spent" })
        {
            var before = Before(); var after = After();
            switch (loss)
            {
                case "fish_removed": after.fish = [Reward()]; break;
                case "breeding_use": after.fish[0].remaining_breeds--; break;
                case "lock_removed": before.fish[0].locked = true; break;
                case "heart_spent": after.hearts--; break;
            }
            bool rejected = false;
            try { LevelUpGuard.Confirm(before, after); } catch (UnexpectedConsumptionException) { rejected = true; }
            check(rejected, "actual loss remains a hard stop: " + loss);
        }

        var optional = new OptionalFeature(); int attempts = 0;
        check(optional.Run(() => { attempts++; throw new IOException("reward UI unavailable"); }) && optional.Paused,
            "optional exception is retained locally without escaping to global request state");
        check(!optional.Run(() => attempts++) && attempts == 1, "deferred level-up is not repeated");
        optional = new(); optional.Restore(true);
        check(optional.Paused, "restart with level journal only defers leveling");
        var cfg = new Settings { enabled = true, mode = "protected" };
        var available = new Snapshot { hearts = 5, capacity = 99, fish = [Ordinary(), Ordinary()] };
        available.fish[0].id = "a"; available.fish[1].id = "b";
        foreach (var fish in available.fish) fish.ready = true;
        ExecutionGuard.Validate(new() { request_id = Guid.NewGuid().ToString(), kind = "breed", fish_ids = ["a", "b"] }, available, cfg);
        check(true, "level journal does not prevent independent breeding");
        var hardFailure = new OptionalFeature(); bool propagated = false;
        try { hardFailure.Run(() => throw new UnexpectedConsumptionException("protected fish lost")); }
        catch (UnexpectedConsumptionException) { propagated = true; }
        check(propagated, "optional wrapper never hides actual resource loss");
        var progress = new OptionalFeature();
        progress.Run(() => check(LevelUpGuard.Confirm(Before(), After()), "normal level completion accepted"));
        check(!progress.Paused, "normal changes do not suspend leveling");
    }
}
