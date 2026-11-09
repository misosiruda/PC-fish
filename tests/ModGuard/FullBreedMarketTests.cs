using PCFish.Automation;

internal static class FullBreedMarketTests
{
    internal static void Run(Action<bool, string> check)
    {
        Settings Config() => new() { enabled = true, mode = "protected", protection_mode = "market", market_merge_price_basis = "full_breeds" };
        FishState Fish(string id) => new() { id = id, species = "A", variant = "v", grade = 1, level = 1,
            remaining_breeds = 3, maximum_breeds = 5, ready = true, market_hash_name = "Example Fish ★" };
        MarketReview Review(string id) => new() { id = id, species = "A", variant = "v", grade = 1,
            remaining_breeds = 3, maximum_breeds = 5, market_hash_name = "Example Fish ★", currency_id = 16,
            checked_at = 1000, valid_until = 87400, product_listings = 20, full_breed_listings = 2, product_max_seller_proceeds_krw = 1000 };
        MarketReport Report(MarketReview review) => new() { schema_version = 1, session = "test", config_digest = "cfg", fish = [review] };
        var cfg = Config(); var f = Fish("a"); var evidence = Review("a");
        MarketProtection.Apply(f, Report(evidence), cfg, "test", "cfg", 1100);
        check(f.market_state == "pending" && f.market_merge_state == "release", "maximum observed ask at 1000 releases merge while exact breed quote remains pending");
        evidence.product_max_seller_proceeds_krw = 1000.01;
        MarketProtection.Apply(f, Report(evidence), cfg, "test", "cfg", 1100);
        check(f.market_merge_state == "keep", "potential above target remains protected");
        foreach (var bad in new[] { "no_full", "invalid_count", "stale", "future", "currency", "star", "variant", "nan", "identity" })
        {
            f = Fish("a"); evidence = Review("a");
            switch (bad)
            {
                case "no_full": evidence.full_breed_listings = 0; break;
                case "invalid_count": evidence.full_breed_listings = 21; break;
                case "stale": evidence.checked_at = 0; evidence.valid_until = 1100; break;
                case "future": evidence.checked_at = 1101; break;
                case "currency": evidence.currency_id = 1; break;
                case "star": evidence.grade = 5; break;
                case "variant": evidence.variant = "other"; break;
                case "nan": evidence.product_max_seller_proceeds_krw = double.NaN; break;
                case "identity": evidence.market_hash_name = "Other Fish ★"; break;
            }
            MarketProtection.Apply(f, Report(evidence), cfg, "test", "cfg", 1100);
            check(f.market_merge_state == "pending", "ignore ungrounded potential: " + bad);
        }
        f = Fish("a"); evidence = Review("a"); evidence.matching_listings = 1; evidence.seller_proceeds_krw = 1000;
        MarketProtection.Apply(f, Report(evidence), cfg, "test", "cfg", 1100);
        check(f.market_state == "keep" && !MarketProtection.CanLock(f, cfg, new() { captured_at = 1100 }),
            "current 1000 ask does not force a lock when best observed state is only 1000");
        var state = new Snapshot { captured_at = 1100, hearts = 5, capacity = 999,
            fish = Enumerable.Range(0, 10).Select(i => Fish(i.ToString())).ToArray() };
        foreach (var material in state.fish) MarketProtection.Apply(material, Report(Review(material.id)), cfg, "test", "cfg", 1100);
        var merge = new Command { request_id = Guid.NewGuid().ToString(), kind = "merge", fish_ids = state.fish.Select(x => x.id).ToArray() };
        ExecutionGuard.Validate(merge, state, cfg); check(true, "native guard allows nonexhausted low-potential surplus");
        foreach (var protection in new[] { "locked", "manual", "expired", "valuable" })
        {
            var guardedCfg = Config(); var material = state.fish[0];
            if (protection == "locked") material.locked = true;
            if (protection == "manual") guardedCfg.reserved_ids = [material.id];
            if (protection == "expired") material.market_merge_valid_until = 1100;
            if (protection == "valuable") material.market_merge_state = "keep";
            bool rejected = false;
            try { ExecutionGuard.Validate(merge, state, guardedCfg); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "surplus merge still honors " + protection);
            material.locked = false; material.market_merge_valid_until = 87400; material.market_merge_state = "release";
        }
        state.season = new() { id = 2, active = true, starts_at = 1, ends_at = 10000, recipes = [new()
            { season = 2, species = "S", grade = 1, ingredients = [new() { species = "A", grade = 1, amount = 1 }] }] };
        cfg.auto_season_craft = true; cfg.season_target_grade = 1;
        state.fish[0].market_state = "keep";
        var plan = SeasonPolicy.Build(state, cfg);
        check(plan.reserved_ids.Length == 1 && plan.next_craft != null, "season quantity and crafting remain available under full-breed value policy");
        bool seasonProtected = false;
        try { ExecutionGuard.Validate(merge, state, cfg); } catch (InvalidDataException) { seasonProtected = true; }
        check(seasonProtected, "surplus merge cannot consume even one season-reserved ingredient");

        var observedCfg = Config(); observedCfg.market_merge_price_basis = "best_observed_breeds";
        observedCfg.Validate();
        MarketReview ObservedReview(string id)
        {
            var r = Review(id); r.full_breed_listings = 0; r.best_observed_remaining_breeds = 1;
            r.best_observed_listings = 2; r.best_observed_seller_proceeds_krw = 1000;
            r.product_max_seller_proceeds_krw = 2000; // A worse state may have a higher asking price.
            r.book_complete = true; r.coverage_version = 2; r.listing_total = r.listing_observed = r.product_listings;
            return r;
        }
        f = Fish("a"); evidence = ObservedReview("a");
        MarketProtection.Apply(f, Report(evidence), observedCfg, "test", "cfg", 1100);
        check(f.market_merge_state == "release" && f.market_best_seller_proceeds_krw == 1000,
            "actual best observed state releases at threshold without hypothetical full-capacity listing");
        evidence.matching_listings = 1; evidence.seller_proceeds_krw = 1000;
        MarketProtection.Apply(f, Report(evidence), observedCfg, "test", "cfg", 1100);
        check(f.market_state == "keep" && !MarketProtection.CanLock(f, observedCfg, new() { captured_at = 1100 }),
            "current asking price does not force automatic locking under best-observed value policy");
        evidence.best_observed_remaining_breeds = 0;
        MarketProtection.Apply(f, Report(evidence), observedCfg, "test", "cfg", 1100);
        check(f.market_merge_state == "release", "zero remaining is best when all observed listings are exhausted");
        evidence.best_observed_seller_proceeds_krw = 1000.01;
        MarketProtection.Apply(f, Report(evidence), observedCfg, "test", "cfg", 1100);
        check(f.market_merge_state == "keep", "best actually observed state above threshold stays protected");
        evidence = ObservedReview("a"); evidence.book_complete = false; evidence.matching_listings = 1; evidence.seller_proceeds_krw = 1000;
        MarketProtection.Apply(f, Report(evidence), observedCfg, "test", "cfg", 1100);
        check(f.market_state == "keep" && !MarketProtection.CanLock(f, observedCfg, new() { captured_at = 1100 }),
            "first-page exact price cannot permanently lock before best-state coverage");
        foreach (var bad in new[] { "missing_state", "no_listings", "bad_count", "missing_price", "nan", "stale", "currency", "first_page", "unread_rows", "old_coverage" })
        {
            evidence = ObservedReview("a");
            switch (bad)
            {
                case "missing_state": evidence.best_observed_remaining_breeds = -1; break;
                case "no_listings": evidence.best_observed_listings = 0; break;
                case "bad_count": evidence.best_observed_listings = 21; break;
                case "missing_price": evidence.best_observed_seller_proceeds_krw = null; break;
                case "nan": evidence.best_observed_seller_proceeds_krw = double.NaN; break;
                case "stale": evidence.valid_until = 1100; break;
                case "currency": evidence.currency_id = 1; break;
                case "first_page": evidence.book_complete = false; break;
                case "unread_rows": evidence.listing_total = 835; evidence.listing_observed = 20; break;
                case "old_coverage": evidence.coverage_version = 1; break;
            }
            MarketProtection.Apply(f, Report(evidence), observedCfg, "test", "cfg", 1100);
            check(f.market_merge_state == "pending", "best observed evidence still requires valid data: " + bad);
        }
        var observedState = new Snapshot { captured_at = 1100, hearts = 0, capacity = 999,
            fish = Enumerable.Range(0, 10).Select(i => Fish(i.ToString())).ToArray() };
        foreach (var material in observedState.fish)
            MarketProtection.Apply(material, Report(ObservedReview(material.id)), observedCfg, "test", "cfg", 1100);
        var observedMerge = new Command { request_id = Guid.NewGuid().ToString(), kind = "merge", fish_ids = observedState.fish.Select(x => x.id).ToArray() };
        ExecutionGuard.Validate(observedMerge, observedState, observedCfg);
        check(true, "native pre-dispatch guard permits new observed-state merge policy with unused parent capacity");
        observedState.season = state.season;
        observedCfg.auto_season_craft = true; observedCfg.season_target_grade = 1;
        observedState.fish[0].market_state = "keep";
        var observedSeason = SeasonPolicy.Build(observedState, observedCfg);
        check(observedSeason.reserved_ids.Length == 1 && observedSeason.next_craft != null,
            "new observed-state policy also drives ordinary material valuation for season crafting");
        bool observedSeasonProtected = false;
        try { ExecutionGuard.Validate(observedMerge, observedState, observedCfg); } catch (InvalidDataException) { observedSeasonProtected = true; }
        check(observedSeasonProtected, "new observed-state surplus merge preserves season reservation");
    }
}
