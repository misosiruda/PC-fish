using PCFish.Automation;

internal static class SeasonPriorityTests
{
    const string Jelly="FS00040",Franken="FS00039",Angler="FS00038";
    internal static void Run(Action<bool,string> check)
    {
        Settings Config() => new() {enabled=true,mode="protected",auto_season_craft=true,season_goal="collection",season_target_grade=2,protection_mode="market"};
        FishState Fish(string id,string species,int grade=1,bool seasonal=false) => new() {id=id,species=species,grade=grade,level=2,
            seasonal=seasonal,remaining_breeds=0,ready=true,market_state="release",market_valid_until=9999};
        SeasonRecipe Recipe(string type,int grade) => new() {season=2,species=type,name=type,grade=grade,
            ingredients=grade==1?[new() {species=type+"-material",grade=1,amount=1}]:
                [new() {species=type,grade=1,amount=1,seasonal=true},new() {species=type+"-material",grade=2,amount=1}]};
        Snapshot State() => new Snapshot {captured_at=1000,hearts=1,capacity=999,
            season=new() {id=2,active=true,starts_at=100,ends_at=9999,
                recipes=new[]{Angler,Franken,Jelly}.SelectMany(t=>new[]{Recipe(t,1),Recipe(t,2)}).ToArray()},
            fish=new[]{Angler,Franken,Jelly}.SelectMany(t=>new[]{Fish(t+"-one",t+"-material"),Fish(t+"-two",t+"-material",2)}).ToArray()}.WithCatalog();
        Command Craft(string type,int grade,string[] ids) => new() {request_id=Guid.NewGuid().ToString(),kind="craft",season=2,
            target_species=type,target_grade=grade,fish_ids=ids};
        var s=State();var c=Config();var p=SeasonPolicy.Build(s,c);
        check(p.focus_species==Jelly&&p.species_priority.SequenceEqual(new[]{Jelly,Franken,Angler}),"user value order selects jellyfish first");
        check(p.next_craft?.species==Jelly&&p.next_craft.grade==1,"jellyfish precursor is made before lower-value species");
        check(p.needs.All(n=>n.species==Jelly+"-material")&&p.reserved_ids.Any(id=>id.StartsWith(Franken)),"focused needs are separate from all-species material reservations");
        s.fish=s.fish.Where(f=>f.species!=Jelly+"-material").ToArray();p=SeasonPolicy.Build(s,c);
        check(p.state=="waiting"&&p.next_craft==null&&p.focus_species==Jelly,"missing jellyfish ingredients do not switch focus to a ready cheaper fish");
        check(p.reserved_ids.Length>0&&p.needs.All(n=>n.reserved==0),"lower-priority stock remains protected while focused material is missing");
        var shared=State();var sharedCfg=Config();sharedCfg.season_target_grade=1;
        shared.season.recipes.Single(r=>r.species==Jelly&&r.grade==1).ingredients=
            [new() {species="shared",grade=1,amount=1},new() {species="missing-jelly",grade=1,amount=1}];
        shared.season.recipes.Single(r=>r.species==Franken&&r.grade==1).ingredients=[new() {species="shared",grade=1,amount=1}];
        shared.fish=[Fish("shared-one","shared")];shared.WithCatalog();var sharedPlan=SeasonPolicy.Build(shared,sharedCfg);
        check(sharedPlan.focus_species==Jelly&&sharedPlan.reserved_ids.Contains("shared-one")&&sharedPlan.next_craft==null,
            "a ready lower-priority craft cannot spend shared material reserved for incomplete jellyfish");
        s.fish=s.fish.Append(Fish("stored-angler",Angler,2,true)).ToArray();p=SeasonPolicy.Build(s,c);
        check(p.reserved_ids.Contains("stored-angler"),"already collected lower-priority seasonal fish remain preserved");
        s=State();s.fish=s.fish.Append(Fish("ready-frank-precursor",Franken,1,true)).ToArray();p=SeasonPolicy.Build(s,c);
        check(p.next_craft?.species==Jelly&&p.next_craft.grade==1,"jellyfish 1-star path wins over a lower-priority higher-star craft");
        bool rejected=false;
        try{ExecutionGuard.Validate(Craft(Franken,1,[Franken+"-one"]),s,c);}catch(InvalidDataException){rejected=true;}
        check(rejected,"native execution guard rejects a stale lower-priority craft");
        s=State();s.fish=s.fish.Append(Fish("jelly-two",Jelly,2,true)).ToArray();p=SeasonPolicy.Build(s,c);
        check(p.focus_species==Jelly&&p.reserved_ids.Contains("jelly-two"),"collection priority waits for every requested grade, not only the highest");
        s.fish=s.fish.Append(Fish("jelly-one",Jelly,1,true)).ToArray();p=SeasonPolicy.Build(s,c);
        check(p.focus_species==Franken&&p.next_craft?.species==Franken,"completed jellyfish collection advances to Frankenfish");
        check(p.reserved_ids.Contains("jelly-one")&&p.reserved_ids.Contains("jelly-two"),"finished jellyfish collection is not recycled when focus advances");
        s.fish=s.fish.Concat(new[]{Fish("frank-one",Franken,1,true),Fish("frank-two",Franken,2,true)}).ToArray();p=SeasonPolicy.Build(s,c);
        check(p.focus_species==Angler&&p.next_craft?.species==Angler,"completed Frankenfish collection advances to anglerfish");
        s.fish=s.fish.Concat(new[]{Fish("angler-one",Angler,1,true),Fish("angler-two",Angler,2,true)}).ToArray();p=SeasonPolicy.Build(s,c);
        check(p.phase=="repeat"&&p.focus_species==Jelly&&p.needs.Length>0,"all collections complete then repeat highest-priority fish");
        s=State();c=Config();c.season_species_priority=[Franken,Jelly,Angler];p=SeasonPolicy.Build(s,c);
        check(p.focus_species==Franken&&p.next_craft?.species==Franken,"explicit configured order is applied without inferring market prices");
        s=State();c=Config();s.fish=Enumerable.Range(0,10).Select(i=>Fish("low-"+i,Angler+"-material")).ToArray();
        p=SeasonPolicy.Build(s,c);
        bool lowRejected=false;
        try { ExecutionGuard.Validate(new() {request_id=Guid.NewGuid().ToString(),kind="merge",fish_ids=s.fish.Select(f=>f.id).ToArray()},s,c); } catch(InvalidDataException) { lowRejected=true; }
        check(p.reserved_ids.Length>0&&lowRejected,"lower-priority seasonal materials cannot be consumed by normal merge");
        c.reserved_ids=["low-0"];bool manualBlocked=false;
        try{ExecutionGuard.Validate(new() {request_id=Guid.NewGuid().ToString(),kind="merge",fish_ids=s.fish.Select(f=>f.id).ToArray()},s,c);}catch(InvalidDataException){manualBlocked=true;}
        check(manualBlocked,"priority never clears a manual reservation");
        foreach(var bad in new[]{"null","duplicate","empty"})
        {
            c=Config();c.season_species_priority=bad=="null"?null!:bad=="duplicate"?[Jelly,Jelly]:[""];
            bool invalid=false;try{c.Validate();}catch(InvalidDataException){invalid=true;}
            check(invalid,"invalid seasonal priority settings: "+bad);
        }
    }
}
