using System.Text.Json;

namespace PCFish.Automation;

internal sealed class RequestWireDiagnostic
{
    public string parse_state { get; set; } = "";
    public string[] known_keys { get; set; } = [];
    public string parent_value_kind { get; set; } = "missing";
    public int parent_count { get; set; }
    public bool matches_command { get; set; }
}

internal static class RequestWireContract
{
    // These are the actual pinned native anonymous-type properties used by
    // FishBreed/FishMerge/FishCraft, rather than a guessed backend schema.
    internal static RequestWireDiagnostic Inspect(Command cmd, string body)
    {
        var d = new RequestWireDiagnostic();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            d.parse_state = root.ValueKind == JsonValueKind.Object ? "json" : "not_object";
            if (root.ValueKind == JsonValueKind.Object)
            {
                d.known_keys = new[] { "parentFishId", "fishIds", "requireFishType", "requireFishGrade", "linkId" }.Where(k => root.TryGetProperty(k, out _)).ToArray();
                if (root.TryGetProperty("parentFishId", out var ids))
                {
                    d.parent_value_kind = ids.ValueKind.ToString();
                    if (ids.ValueKind == JsonValueKind.Array)
                    {
                        d.parent_count = ids.GetArrayLength();
                        d.matches_command = ids.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                            && ids.EnumerateArray().Select(x => x.GetString()).SequenceEqual(cmd.fish_ids);
                    }
                }
                if (cmd.kind == "craft")
                    d.matches_command &= root.TryGetProperty("requireFishType", out var species) && species.ValueKind == JsonValueKind.String && species.GetString() == cmd.target_species
                        && root.TryGetProperty("requireFishGrade", out var grade) && grade.ValueKind == JsonValueKind.Number && grade.TryGetInt32(out int n) && n == cmd.target_grade;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { d.parse_state = "invalid_json"; }
        return d;
    }
}
