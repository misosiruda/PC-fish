using PCFish.Automation;

internal static class BuyOrderTests
{
    internal static BuyOrderProof Proof(string name = "Example ★", long bid = 115000) => new()
    {
        appid = 4842670, currency_id = 16, market_hash_name = name, fee_model = "steam_krw_round_10_v1",
        highest_buy_order_minor = bid, buy_order_count = bid == 0 ? 0 : 3,
        buy_orders = bid == 0 ? [] : [new() { price_minor = bid, quantity = 3 }],
        fee_samples = [new() { seller_minor = 218000, steam_fee_minor = 11000, publisher_fee_minor = 22000 }]
    };
    internal static void Run(Action<bool, string> check)
    {
        var cfg = new Settings { enabled = true, mode = "protected", protection_mode = "market", market_merge_price_basis = BuyOrderProof.Basis };
        cfg.Validate();
        FishState Fish() => new() { id = "a", species = "FS00001", variant = "v", level = 1, grade = 1,
            remaining_breeds = 5, maximum_breeds = 5, ready = true, market_hash_name = "Example ★" };
        MarketReport Report(long bid = 115000) => new() { schema_version = 1, session = "test", config_digest = "cfg", fish = [new()
        { id = "a", species = "FS00001", variant = "v", grade = 1, remaining_breeds = 5, maximum_breeds = 5,
            market_hash_name = "Example ★", checked_at = 1000, valid_until = 87400, currency_id = 16,
            price_basis = BuyOrderProof.Basis, buy_order_proof = Proof(bid:bid), seller_proceeds_krw = 1,
            best_observed_seller_proceeds_krw = 999999 } ] };
        foreach (var (bid, net, state) in new[] { (0L, 0d, "release"), (4000L, 20d, "release"),
            (115000L, 1000d, "release"), (116000L, 1010d, "keep"), (169000L, 1470d, "keep") })
        {
            var f = Fish(); MarketProtection.Apply(f, Report(bid), cfg, "test", "cfg", 1100);
            check(f.market_state == state && f.market_merge_state == state && f.market_seller_proceeds_krw == net,
                "native recomputes bid net, ignores claimed price/asks: " + bid);
        }
        foreach (var bad in new[] { "missing", "currency", "name", "app", "count", "top", "fee", "empty_fees", "negative", "model", "stale", "future", "legacy" })
        {
            var f = Fish(); var r = Report(); var evidence = r.fish[0]; var p = evidence.buy_order_proof;
            switch (bad)
            {
                case "missing": evidence.buy_order_proof = null!; break;
                case "currency": p.currency_id = 1; break;
                case "name": p.market_hash_name = "Wrong"; break;
                case "app": p.appid = 1; break;
                case "count": p.buy_order_count = 2; break;
                case "top": p.highest_buy_order_minor = 4000; break;
                case "fee": p.fee_samples[0].steam_fee_minor = 0; break;
                case "empty_fees": p.fee_samples = []; break;
                case "negative": p.highest_buy_order_minor = -1; break;
                case "model": p.fee_model = "old"; break;
                case "stale": evidence.valid_until = 1100; break;
                case "future": evidence.checked_at = 1101; break;
                case "legacy": evidence.price_basis = "best_observed_breeds"; break;
            }
            MarketProtection.Apply(f, r, cfg, "test", "cfg", 1100);
            check(f.market_state == "pending" && f.market_merge_state == "pending", "invalid buy order protected: " + bad);
        }
        var materials = Enumerable.Range(0,10).Select(i => { var f=Fish(); MarketProtection.Apply(f,Report(),cfg,"test","cfg",1100); f.id=i.ToString();return f; }).ToArray();
        var s = new Snapshot { captured_at=1100, capacity=999, fish=materials };
        var cmd = new Command { request_id=Guid.NewGuid().ToString(), kind="merge", fish_ids=materials.Select(f=>f.id).ToArray() };
        ExecutionGuard.Validate(cmd,s,cfg);check(true,"highest-bid release reaches execution");
        foreach(var field in new[]{"locked","seasonal","manual"})
        {
            materials[0].locked=field=="locked";materials[0].seasonal=field=="seasonal";cfg.reserved_ids=field=="manual"?[materials[0].id]:[];
            bool rejected=false;try{ExecutionGuard.Validate(cmd,s,cfg);}catch(InvalidDataException){rejected=true;}
            check(rejected,"bid pricing preserves "+field);
        }
        MarketCatalogEntry Entry(string species,string name)=>new(){species=species,level=1,market_names=Enumerable.Range(1,5).Select(g=>name+" "+new string('★',g)).ToArray()};
        var catalog=new[]{Entry("FS00041","New"),Entry("FS00001","Old")};
        var target=new FishState {species="FS00041",level=1,grade=1,market_hash_name="New ★"};
        var peer=new MarketPeerQuote {species="FS00001",grade=1,market_hash_name="Old ★",currency_id=16,checked_at=1000,valid_until=87400,
            price_basis=BuyOrderProof.Basis,buy_order_proof=Proof("Old ★"),seller_proceeds_krw=1000};
        check(UnlistedMarketValue.Apply(target,[peer],catalog,cfg,1100)&&target.market_estimated_seller_proceeds_krw==1000
            &&target.market_value_source==BuyOrderProof.EstimateSource,"unlisted highest-bid peer estimate");
        peer.seller_proceeds_krw=1;
        check(!UnlistedMarketValue.Apply(target,[peer],catalog,cfg,1100),"native rejects underpriced peer claim");
        peer.seller_proceeds_krw=1000;peer.buy_order_proof=null!;
        check(!UnlistedMarketValue.Apply(target,[peer],catalog,cfg,1100),"legacy ask peer cannot release under bid policy");
    }
}
