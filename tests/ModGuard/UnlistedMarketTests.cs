using PCFish.Automation;

internal static class UnlistedMarketTests
{
    internal static void Run(Action<bool, string> check)
    {
        var cfg = new Settings { enabled = true, mode = "protected", protection_mode = "market", market_merge_price_basis = "best_observed_breeds" };
        MarketCatalogEntry Entry(string species, string name, int level = 1, bool seasonal = false) => new()
        { species = species, level = level, seasonal = seasonal, market_names = Enumerable.Range(1, 5).Select(g => name + " " + new string('★', g)).ToArray() };
        MarketCatalogEntry[] catalog = [Entry("FS00041", "Frostblade"), Entry("FS00001", "Old A"), Entry("FS00002", "Old B"),
            Entry("FS00011", "Wrong Rarity", 2), Entry("FS00042", "Other New"), Entry("FS00038", "Seasonal", 1, true)];
        MarketPeerQuote Peer(string species = "FS00001", string name = "Old A ★", double price = 20) => new()
        { species = species, grade = 1, market_hash_name = name, checked_at = 1000, valid_until = 87400, currency_id = 16,
            best_remaining_breeds = 2, best_maximum_breeds = 5, seller_proceeds_krw = price,
            book_complete = true, coverage_version = 2, listing_total = 100, listing_observed = 100 };
        FishState Fish(string id = "new") => new() { id = id, species = "FS00041", variant = "v", level = 1, grade = 1,
            remaining_breeds = 2, maximum_breeds = 3, market_hash_name = "Frostblade ★", ready = true };
        MarketReport Report(string id = "new") => new() { schema_version = 1, session = "test", config_digest = "cfg",
            fish = [new() { id = id, species = "FS00041", variant = "v", grade = 1, remaining_breeds = 2, maximum_breeds = 3,
                market_hash_name = "Frostblade ★", currency_id = 16, value_source = UnlistedMarketValue.Source }],
            peer_quotes = [Peer(), Peer("FS00002", "Old B ★", 50), Peer("FS00011", "Wrong Rarity ★", 9000)] };
        var f = Fish(); var report = Report();
        MarketProtection.Apply(f, report, cfg, "test", "cfg", 1100, catalog);
        check(f.market_state == "release" && f.market_merge_state == "release" && f.market_estimated_seller_proceeds_krw == 50
            && f.market_estimate_peer_count == 2 && f.market_seller_proceeds_krw == null && f.market_best_seller_proceeds_krw == null,
            "native recomputes same-rarity/star peer maximum and keeps estimate separate from real quote");
        foreach (var price in new[] { 1000d, 1000.01 })
        {
            f = Fish(); report = Report(); report.peer_quotes[1].seller_proceeds_krw = price;
            MarketProtection.Apply(f, report, cfg, "test", "cfg", 1100, catalog);
            check(f.market_merge_state == (price <= 1000 ? "release" : "keep"), "native estimate threshold matches planner");
            if (price > 1000) check(!MarketProtection.CanLock(f, cfg, new() { captured_at = 1100 }), "estimate never permanently toggles a native lock");
        }
        foreach (var price in new[] { 5d, 1500d })
        {
            f = Fish(); report = Report(); var observed = report.fish[0];
            observed.value_source = "observed"; observed.checked_at = 1000; observed.valid_until = 87400;
            observed.product_listings = observed.best_observed_listings = 1;
            observed.best_observed_remaining_breeds = 2; observed.best_observed_seller_proceeds_krw = price;
            observed.book_complete = true; observed.coverage_version = 2; observed.listing_total = observed.listing_observed = 1;
            MarketProtection.Apply(f, report, cfg, "test", "cfg", 1100, catalog);
            check(f.market_value_source == "observed" && f.market_estimated_seller_proceeds_krw == null
                && f.market_merge_state == (price <= 1000 ? "release" : "keep"), "actual new-species price takes precedence over estimate");
        }
        foreach (var bad in new[] { "stale", "future", "currency", "star", "name", "breed", "nan", "new_peer", "season_peer", "unknown_peer", "session", "identity", "no_catalog", "target_season", "target_level", "first_page", "unread_rows", "old_coverage" })
        {
            f = Fish(); report = Report(); report.peer_quotes = [Peer(), Peer("FS00002", "Old B ★", 50)];
            var p = report.peer_quotes[0]; var useCatalog = catalog;
            switch (bad)
            {
                case "stale": p.valid_until = 1100; break;
                case "future": p.checked_at = 1101; break;
                case "currency": p.currency_id = 1; break;
                case "star": p.grade = 2; break;
                case "name": p.market_hash_name = "Wrong ★"; break;
                case "breed": p.best_remaining_breeds = 6; break;
                case "nan": p.seller_proceeds_krw = double.NaN; break;
                case "new_peer": p.species = "FS00042"; p.market_hash_name = "Other New ★"; break;
                case "season_peer": p.species = "FS00038"; p.market_hash_name = "Seasonal ★"; break;
                case "unknown_peer": p.species = "UNKNOWN"; break;
                case "session": report.session = "other"; break;
                case "identity": report.fish[0].remaining_breeds = 1; break;
                case "no_catalog": useCatalog = []; break;
                case "target_season": f.seasonal = true; break;
                case "target_level": f.level = 2; break;
                case "first_page": p.book_complete = false; break;
                case "unread_rows": p.listing_observed = 20; break;
                case "old_coverage": p.coverage_version = 1; break;
            }
            MarketProtection.Apply(f, report, cfg, "test", "cfg", 1100, useCatalog);
            check(f.market_merge_state == "pending", "invalid estimate evidence stays local: " + bad);
        }
        var state = new Snapshot { session = "test", captured_at = 1100, capacity = 999, hearts = 1,
            season = new(), fish = Enumerable.Range(0, 10).Select(i => Fish(i.ToString())).ToArray() };
        foreach (var material in state.fish) MarketProtection.Apply(material, Report(material.id), cfg, "test", "cfg", 1100, catalog);
        foreach (var material in state.fish) material.remaining_breeds = 0;
        var cmd = new Command { kind = "merge", request_id = Guid.NewGuid().ToString(), fish_ids = state.fish.Select(x => x.id).ToArray() };
        ExecutionGuard.Validate(cmd, state, cfg); check(true, "estimated low-value fish can actually pass native merge guard");
        foreach (var protect in new[] { "lock", "reservation", "season", "expiry" })
        {
            cfg.reserved_ids = []; state.fish[0].locked = false; state.fish[0].seasonal = false; state.fish[0].market_merge_valid_until = 87400;
            switch (protect) { case "lock": state.fish[0].locked = true; break; case "reservation": cfg.reserved_ids = ["0"]; break;
                case "season": state.fish[0].seasonal = true; break; case "expiry": state.fish[0].market_merge_valid_until = 1100; break; }
            bool denied = false; try { ExecutionGuard.Validate(cmd, state, cfg); } catch (InvalidDataException) { denied = true; }
            check(denied, "estimate retains native protection: " + protect);
        }
        state.fish[0].market_merge_valid_until = 87400;
        cfg.auto_season_craft = true; cfg.season_target_grade = 1;
        state.season = new() { id = 2, active = true, starts_at = 1, ends_at = 10000, recipes = [new()
            { season = 2, species = "SEASONAL", grade = 1, ingredients = [new() { species = "FS00041", grade = 1, amount = 1 }] }] };
        state.WithCatalog();
        bool recipeDenied = false; try { ExecutionGuard.Validate(cmd, state, cfg); } catch (InvalidDataException) { recipeDenied = true; }
        check(recipeDenied, "estimated material still belongs to native exact season reservation");

        var futureCatalog = catalog.Append(Entry("FUTURE", "Future Fish")).ToArray();
        MarketReport FutureReport()
        {
            var r = Report(); r.fish[0].species = "FUTURE"; r.fish[0].market_hash_name = "Future Fish ★";
            r.season_evidence = new() { state = "verified", season = 3,
                announcement = new() { season = 3, gid = "123456", published_at = 1000,
                    url = "https://steamcommunity.com/games/4842670/announcements/detail/123456" },
                new_regular_species = ["FUTURE"], new_species = [new() { species = "FUTURE", name = "Future Fish", identification = "native_catalog_delta" }] };
            return r;
        }
        foreach (var scenario in new[] { "valid", "wrong_season", "wrong_app", "non_steam", "old_baseline", "name_mismatch", "not_verified", "future_publication" })
        {
            f = Fish(); f.species = "FUTURE"; f.market_hash_name = "Future Fish ★"; report = FutureReport();
            switch (scenario)
            {
                case "wrong_season": report.season_evidence.season = 2; break;
                case "wrong_app": report.season_evidence.announcement.url = "https://steamcommunity.com/games/1/announcements/detail/123456"; break;
                case "non_steam": report.season_evidence.announcement.url = "https://example.com/games/4842670/announcements/detail/123456"; break;
                case "old_baseline": report.season_evidence.new_species[0].identification = "initial_catalog_baseline"; break;
                case "name_mismatch": report.season_evidence.new_species[0].name = "Wrong Fish"; break;
                case "not_verified": report.season_evidence.state = "awaiting_official_season_announcement"; break;
                case "future_publication": report.season_evidence.announcement.published_at = 1101; break;
            }
            MarketProtection.Apply(f, report, cfg, "test", "cfg", 1100, futureCatalog, 3);
            check(f.market_merge_state == (scenario == "valid" ? "release" : "pending"), "future native cohort article and catalog proof: " + scenario);
        }
    }
}
