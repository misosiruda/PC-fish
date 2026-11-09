using PCFish.Automation;

internal static class SellingStateTests
{
    internal static void Run(Action<bool,string> check)
    {
        SellingStatePrefix State(int n,int count,double price) => new() { remaining_breeds=n,listing_total=count,
            query=$"\"Breeds: {n} /\"",sort_field=0,sort_direction=1,
            prefix_seller_prices=Enumerable.Range(0,n==2?count:1).Select(i=>i==0?price:100d).ToArray(),
            prefix_remaining_breeds=Enumerable.Repeat(n,n==2?count:1).ToArray(),prefix_maximum_breeds=Enumerable.Repeat(2,n==2?count:1).ToArray(),
            prefix_listing_ids=Enumerable.Range(0,n==2?count:1).Select(i=>$"{n+1}{i}").ToArray() };
        SellingStateProof Proof() => new() { appid=4842670,currency_id=16,market_hash_name="Example ★★★",total_before=835,total_after=835,
            states=[State(0,702,20000),State(1,103,500),State(2,30,4870)] };
        check(SellingStateProof.TryBest(Proof(),"Example ★★★",out var best)&&best.remaining==2&&best.price==4870&&best.listings==30,
            "full population partition proves actual best state, ignoring exhausted first-page price");
        var reversed=Proof();Array.Reverse(reversed.states[2].prefix_seller_prices);
        check(SellingStateProof.TryBest(reversed,"Example ★★★",out var varied)&&varied.price==4870,"price order variation still uses complete highest-state maximum");
        foreach(var bad in new[]{"count","gap","name","currency","query","direction","wrong_state","nan","duplicate","incomplete"})
        {
            var p=Proof();switch(bad)
            {
                case "count":p.total_after=836;break;case "gap":p.states[1].remaining_breeds=3;break;case "name":p.market_hash_name="Other";break;
                case "currency":p.currency_id=1;break;case "query":p.states[2].query="other";break;case "direction":p.states[2].sort_direction=0;break;
                case "wrong_state":p.states[2].prefix_remaining_breeds=[0];break;case "nan":p.states[2].prefix_seller_prices=[double.NaN];break;
                case "duplicate":p.states[2].prefix_listing_ids[1]=p.states[2].prefix_listing_ids[0];break;
                case "incomplete":p.states[2].listing_total++;p.total_before++;p.total_after++;break;
            }
            check(!SellingStateProof.TryBest(p,"Example ★★★",out _),"invalid selling-state proof: "+bad);
        }
        var c=new Settings {enabled=true,mode="protected",protection_mode="market",market_merge_price_basis="best_observed_breeds"};
        var f=new FishState {id="a",species="A",variant="v",grade=3,level=4,remaining_breeds=1,maximum_breeds=2,market_hash_name="Example ★★★"};
        var r=new MarketReview {id="a",species="A",variant="v",grade=3,remaining_breeds=1,maximum_breeds=2,market_hash_name=f.market_hash_name,
            currency_id=16,checked_at=1000,valid_until=87400,coverage_version=3,book_complete=true,best_state_proof=Proof()};
        MarketProtection.Apply(f,new() {schema_version=1,session="s",config_digest="c",fish=[r]},c,"s","c",1100);
        check(f.market_book_complete&&f.market_merge_state=="keep"&&f.market_best_seller_proceeds_krw==4870,
            "native consumer derives price from state proof instead of first-page summaries");
        MarketCatalogEntry Entry(string species,string name) => new() {species=species,level=4,
            market_names=Enumerable.Range(1,5).Select(g=>name+" "+new string('★',g)).ToArray()};
        MarketCatalogEntry[] catalog=[Entry("FS00037","Bloodmoon"),Entry("OLD1","Old One"),Entry("OLD2","Old Two")];
        f=new() {id="new",species="FS00037",variant="v",grade=3,level=4,remaining_breeds=1,maximum_breeds=1,market_hash_name="Bloodmoon ★★★"};
        MarketPeerQuote Peer(string species,string name)
        {
            var p=Proof();p.market_hash_name=name;
            return new() {species=species,grade=3,market_hash_name=name,currency_id=16,checked_at=1000,valid_until=87400,
                book_complete=true,coverage_version=3,listing_total=835,listing_observed=20,best_remaining_breeds=2,best_maximum_breeds=2,
                seller_proceeds_krw=4870,best_state_proof=p};
        }
        var estimate=new MarketReview {id="new",species=f.species,variant="v",grade=3,remaining_breeds=1,maximum_breeds=1,
            market_hash_name=f.market_hash_name,currency_id=16,value_source=UnlistedMarketValue.Source};
        var report=new MarketReport {schema_version=1,session="s",config_digest="c",fish=[estimate],
            peer_quotes=[Peer("OLD1","Old One ★★★"),Peer("OLD2","Old Two ★★★")]};
        MarketProtection.Apply(f,report,c,"s","c",1100,catalog);
        check(f.market_merge_state=="keep"&&f.market_estimated_seller_proceeds_krw==4870&&f.market_estimate_peer_count==2,
            "new unlisted species uses full state-partition peer prices");
        report.peer_quotes[1].seller_proceeds_krw=20;
        MarketProtection.Apply(f,report,c,"s","c",1100,catalog);
        check(f.market_merge_state=="pending","peer scalar cannot replace verified highest-state price");
    }
}
