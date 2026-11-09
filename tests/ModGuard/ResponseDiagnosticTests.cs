using PCFish.Automation;
using System.Text.Json;

internal static class ResponseDiagnosticTests
{
    internal static void Run(Action<bool, string> check)
    {
        var success = ResponseDiagnostics.Read("""{"status":"success","data":{"accessToken":"never-copy-this","fish":[]},"message":"private"}""");
        check(success.status == "success" && success.errors.Count == 0, "success inventory and account payload never recorded");
        var child = ResponseDiagnostics.Read("""{"status":"success","data":{"uid":"12345678-abcd-abcd-abcd-123456789abc","accessToken":"never-copy-this"}}""");
        check(child.child_uid == "12345678-abcd-abcd-abcd-123456789abc" && !JsonSerializer.Serialize(child).Contains("never-copy-this"), "only returned child UID retained for birth correlation");
        var native = ResponseDiagnostics.Read("""{"status":"success","data":"{\"uid\":\"12345678-abcd-abcd-abcd-123456789abc\",\"type\":\"FS00002\",\"fish\":\"FS00002_01_01_03\",\"grade\":1,\"level\":1,\"growth\":0,\"breedCount\":3,\"breedMaxCount\":3,\"breedDate\":\"2026-11-08T01:00:00Z\",\"timestamp\":1794099600,\"accessToken\":\"never-copy-this\"}"}""");
        check(native.child_fields["type"].GetString() == "FS00002" && native.child_fields["breedCount"].GetInt32() == 3 && native.child_fields.Count == 9,
            "native encoded fish receipt keeps only whitelisted scalar evidence");
        var hostile = ResponseDiagnostics.Read("""{"status":"success","data":{"type":"accessToken=secret","fish":{"token":"secret"},"grade":"secret","breedDate":"secret","timestamp":{},"accessToken":"secret"}}""");
        check(hostile.child_fields.Count == 0 && !JsonSerializer.Serialize(hostile).Contains("secret"), "native field names cannot carry arbitrary secret data");
        var failed = ResponseDiagnostics.Read("""{"status":"error","error":{"code":123,"message":"Fish cooldown"},"accessToken":"never-copy-this","data":{"account":"never-copy-this"}}""");
        check(failed.errors["error.code"] == "123" && failed.errors["error.message"] == "Fish cooldown", "error code and reason retained");
        check(!JsonSerializer.Serialize(failed).Contains("never-copy-this"), "unrelated response fields excluded");
        var scrubbed = ResponseDiagnostics.Read("""{"status":"error","message":"accessToken=supersecret refreshToken=hidden password=secret id=12345678-abcd-abcd-abcd-123456789abc https://example.invalid/secret email@example.invalid ABCDEFGHIJKLMNOPQRSTUVWXYZ123456"}""");
        var text = JsonSerializer.Serialize(scrubbed);
        check(!new[] { "supersecret", "hidden", "password=secret", "12345678-abcd", "example.invalid", "ABCDEFGHIJKLMNOPQRSTUVWXYZ" }.Any(text.Contains), "credentials URLs identifiers and long tokens redacted");
        check(ResponseDiagnostics.Read("not json containing secrets").parse_state == "invalid_json", "malformed body omitted");
        check(ResponseDiagnostics.Read("[]").parse_state == "not_object", "unexpected response shape omitted");
        check(ResponseDiagnostics.Read("").parse_state == "empty_or_oversize", "empty body handled");
    }
}
