using System.Text.Json;
using PCFish.Automation;

internal static class PolicyReplay
{
    internal sealed class Input
    {
        public Snapshot snapshot { get; set; } = new();
        public Settings config { get; set; } = new();
        public Command[] commands { get; set; } = [];
    }
    internal static void Run(string input, string output)
    {
        var cases = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(input)) ?? throw new InvalidDataException("Missing replay cases");
        var results = cases.Select(item =>
        {
            item.snapshot.season.plan = SeasonPolicy.Build(item.snapshot, item.config);
            var guards = item.commands.Select(command =>
            {
                try { ExecutionGuard.Validate(command, item.snapshot, item.config); return new { accepted = true, reason = "" }; }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return new { accepted = false, reason = e.Message }; }
            }).ToArray();
            return new { item.snapshot, item.config, guards };
        }).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
