using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCFish.Automation;

internal sealed class ResponseDiagnostic
{
    public string status { get; set; } = "";
    public string parse_state { get; set; } = "";
    public string response_sha256 { get; set; } = "";
    public Dictionary<string, string> errors { get; set; } = new();
    public string child_uid { get; set; } = "";
    public Dictionary<string, JsonElement> child_fields { get; set; } = new();
}

internal static class ResponseDiagnostics
{
    internal static ResponseDiagnostic Read(string response)
    {
        var result = new ResponseDiagnostic { response_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response ?? ""))).ToLowerInvariant() };
        if (string.IsNullOrEmpty(response) || response.Length > 1024 * 1024) { result.parse_state = "empty_or_oversize"; return result; }
        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { result.parse_state = "not_object"; return result; }
            result.parse_state = "json";
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                result.status = Clean(status.GetString() ?? "");
            // Native NewFish's pinned scalar schema only. Keep enough evidence
            // to distinguish an actual fish receipt from an ownership mismatch,
            // without retaining arbitrary account fields or the raw body.
            if (result.status == "success")
            {
                if (root.TryGetProperty("data", out var data))
                {
                    using var childDoc = JsonDocument.Parse(data.ValueKind == JsonValueKind.String ? data.GetString() ?? "null" : data.GetRawText());
                    if (childDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var fish = childDoc.RootElement;
                        if (fish.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.String && Guid.TryParse(uid.GetString(), out _))
                            result.child_uid = uid.GetString() ?? "";
                        foreach (var key in new[] { "fish", "type", "grade", "level", "growth", "breedCount", "breedMaxCount", "breedDate", "breedNextDate", "timestamp" })
                        {
                            if (!fish.TryGetProperty(key, out var value)) continue;
                            bool valid = key is "fish" or "type"
                                ? value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString() ?? "", @"\AFS[0-9_]{1,40}\z")
                                : key is "breedDate" or "breedNextDate"
                                    ? value.ValueKind == JsonValueKind.String && (value.GetString() ?? "").Length <= 64
                                        && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _)
                                    : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _)
                                        || value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out _);
                            if (valid) result.child_fields[key] = value.Clone();
                        }
                    }
                }
                return result;
            }
            foreach (var key in new[] { "code", "errorCode", "error_code", "message", "error", "description", "detail" })
            {
                if (!root.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    result.errors[key] = Clean(value.ToString());
                else if (key == "error" && value.ValueKind == JsonValueKind.Object)
                    foreach (var nested in new[] { "code", "message", "description" })
                        if (value.TryGetProperty(nested, out var scalar) && scalar.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            result.errors["error." + nested] = Clean(scalar.ToString());
            }
        }
        catch (JsonException) { result.parse_state = "invalid_json"; }
        return result;
    }

    static string Clean(string text)
    {
        text = Regex.Replace(text ?? "", @"(?i)(access.?token|refresh.?token|authorization|service.?key|password)\s*[:=]\s*\S+", "$1=[redacted]");
        text = Regex.Replace(text, @"(?i)https?://\S+|\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b|\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b|\b[A-Za-z0-9+/=-]{24,}\b", "[redacted]");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 512 ? text : text[..512];
    }
}
