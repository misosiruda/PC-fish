using PCFish.Automation;

internal static class SameGroupMergeTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var mode in new[] { "protected", "economic" })
        foreach (var protection in new[] { "grade", "market" })
        {
            var cfg = new Settings { enabled = true, mode = mode, protection_mode = protection,
                market_merge_price_basis = "highest_buy_order", protect_grade_at_least = 5 };
            var fish = Enumerable.Range(0, 10).Select(i => new FishState
            {
                id = i.ToString(), species = "species" + i % 2, level = 3, grade = 1,
                remaining_breeds = 0, market_state = "release", market_valid_until = 2000,
                market_merge_state = "release", market_merge_valid_until = 2000
            }).ToArray();
            var snapshot = new Snapshot { captured_at = 1000, capacity = 999, fish = fish };
            var command = new Command { request_id = Guid.NewGuid().ToString(), kind = "merge",
                fish_ids = fish.Select(f => f.id).ToArray() };
            ExecutionGuard.Validate(command, snapshot, cfg);
            check(true, $"same rarity/stars across exhausted species: {mode}/{protection}");
            foreach (var mismatch in new[] { "rarity", "stars" })
            {
                if (mismatch == "rarity") fish[9].level = 1;
                else fish[9].grade = 2;
                var refused = false;
                try { ExecutionGuard.Validate(command, snapshot, cfg); }
                catch (InvalidDataException ex) when (ex.Message == "Merge requires same stars, adjacent rarities and no downgrade risk")
                { refused = true; }
                check(refused, $"live state mismatch refused before dispatch: {mode}/{protection}/{mismatch}");
                fish[9].level = 3; fish[9].grade = 1;
            }
            foreach (var lower in Enumerable.Range(1, 4))
            foreach (var higherCount in Enumerable.Range(1, 9))
            {
                for (var i = 0; i < 10; i++) fish[i].level = i < higherCount ? lower + 1 : lower;
                ExecutionGuard.Validate(command, snapshot, cfg);
                check(true, $"adjacent rarity split accepted: {mode}/{protection}/{lower}/{higherCount}");
            }
            // Even a high-heavy batch whose forecast does not drop is outside the one-step policy.
            foreach (var f in fish) f.level = 3;
            fish[0].level = 1;
            check(!MergePolicy.Allows(fish), "two-step gap refused despite average 2.8");
        }
    }
}
