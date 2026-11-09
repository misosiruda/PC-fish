using System.Text.Json;
using PCFish.Automation;

internal static class RequestWireTests
{
    internal static void Run(Action<bool, string> check)
    {
        var cmd = new Command { kind = "breed", fish_ids = ["a", "b"] };
        foreach (var body in new[] { "{}", "null", "", "{broken", """{"linkId":["a","b"]}""", """{"parentFishId":[]}""", """{"parentFishId":["wrong","b"]}""", """{"parentFishId":null}""" })
        {
            var d = RequestWireContract.Inspect(cmd, body);
            check(!d.matches_command, "missing or mismatched native parent IDs detected: " + body);
        }
        string correct = """{ "parentFishId": ["a", "b"] }""";
        var result = RequestWireContract.Inspect(cmd, correct);
        check(result.matches_command && result.parent_count == 2, "native parent IDs match guarded command");
        cmd.kind = "merge"; cmd.fish_ids = Enumerable.Range(0, 10).Select(x => x.ToString()).ToArray();
        result = RequestWireContract.Inspect(cmd, JsonSerializer.Serialize(new { parentFishId = cmd.fish_ids }));
        check(result.matches_command && result.parent_count == 10, "merge diagnostics compare exact ten parents");
        cmd.kind = "craft"; cmd.target_species = "seasonal"; cmd.target_grade = 3;
        result = RequestWireContract.Inspect(cmd, JsonSerializer.Serialize(new { requireFishType = cmd.target_species, requireFishGrade = cmd.target_grade, parentFishId = cmd.fish_ids }));
        check(result.matches_command, "craft diagnostics compare authoritative recipe identity");
    }
}
