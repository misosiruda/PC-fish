using PCFish.Automation;

internal static class TestCatalog
{
    // Existing minimal recipe fixtures use rarity 2 for every species. New stock
    // tests supply distinct authoritative rarities explicitly.
    internal static Snapshot WithCatalog(this Snapshot s)
    {
        var catalog = new Dictionary<string, SpeciesInfo>();
        foreach (var r in s.season.recipes)
        {
            catalog[r.species] = new() { species = r.species, name = r.name, level = 2, seasonal = true };
            foreach (var i in r.ingredients) catalog[i.species] = new() { species = i.species, name = i.name, level = 2, seasonal = i.seasonal };
        }
        foreach (var f in s.fish) catalog[f.species] = new() { species = f.species, name = f.species, level = f.level, seasonal = f.seasonal };
        s.species_catalog = catalog.Values.ToArray();
        return s;
    }
}
