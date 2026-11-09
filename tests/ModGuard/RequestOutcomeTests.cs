using PCFish.Automation;

internal static class RequestOutcomeTests
{
    internal static void Run(Action<bool, string> check)
    {
        Snapshot State() => new() { hearts = 2, tank_level = 3, tank_exp = 200,
            fish = [new() { id = "a", species = "ordinary", level = 1, grade = 1, remaining_breeds = 2, maximum_breeds = 2, locked = true },
                    new() { id = "b", species = "basic", basic = true, growth = 5 }] };
        bool Confirm(Snapshot after, bool explicitFailure = true, string child = "")
            => RequestOutcomeGuard.CanConfirmRejection(explicitFailure, child, State(), after);
        check(Confirm(State()), "explicit rejection without consumption resumes");
        var normal = State(); normal.hearts++; normal.tank_exp += 10; normal.fish[1].growth = 10;
        normal.fish[0].ready = true; normal.fish[0].placed = true; normal.fish[0].market_state = "pending";
        check(Confirm(normal), "routine recovery feeding and UI changes do not freeze rejection");
        check(!Confirm(State(), false), "network errors and unknown outcomes remain pending");
        check(!Confirm(State(), child: "child"), "a returned child is not a no-consumption rejection");
        foreach (var field in new[] { "missing", "replaced", "added", "uses", "hearts", "lock", "maximum", "species", "grade", "level", "duplicate" })
        {
            var after = State();
            switch (field)
            {
                case "missing": after.fish = [after.fish[0]]; break;
                case "replaced": after.fish[1].id = "other"; break;
                case "added": after.fish = [.. after.fish, new() { id = "child" }]; break;
                case "uses": after.fish[0].remaining_breeds--; break;
                case "hearts": after.hearts--; break;
                case "lock": after.fish[0].locked = false; break;
                case "maximum": after.fish[0].maximum_breeds++; break;
                case "species": after.fish[0].species = "other"; break;
                case "grade": after.fish[0].grade++; break;
                case "level": after.fish[0].level++; break;
                case "duplicate": after.fish[1].id = after.fish[0].id; break;
            }
            check(!Confirm(after), "changed rejection remains pending: " + field);
        }
        var duplicateBefore = State(); duplicateBefore.fish[1].id = "a";
        check(!RequestOutcomeGuard.CanConfirmRejection(true, "", duplicateBefore, State()), "duplicate prior inventory cannot confirm rejection");
    }
}
