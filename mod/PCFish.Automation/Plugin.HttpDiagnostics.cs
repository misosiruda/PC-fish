using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine.Networking;

namespace PCFish.Automation;

public sealed partial class Plugin
{
    readonly HashSet<string> observedHttpCompletions = new();
    string httpDiagnosticNote = "";

    void InstallHttpDiagnostics(Harmony harmony)
    {
        // Observe the completed native SDK request before its callback/disposal.
        // No replacement callback, extra HTTP request, headers or body changes.
        foreach (var name in new[] { "_Get_d__24", "_Put_d__25" })
        {
            var type = typeof(NANOOGame.Network.NetworkManager).GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(name);
            var method = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(name, "MoveNext");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ObserveHttpCompletion), BindingFlags.Static | BindingFlags.NonPublic)));
        }
        httpDiagnosticNote = "Native HTTP completion observation active";
    }

    static void ObserveHttpCompletion(object __instance)
    {
        var p = Current;
        if (p == null || __instance is not Il2CppObjectBase native) return;
        try
        {
            var property = __instance.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.PropertyType == typeof(UnityWebRequest));
            var www = property.GetValue(__instance) as UnityWebRequest;
            if (www == null || !www.isDone) return;
            var path = new Uri(www.url).AbsolutePath;
            if (!new[] { "/cs/pcfish/breed/run", "/cs/pcfish/merge/run", "/cs/pcfish/craft/run", "/cs/pcfish/manage/search" }.Contains(path)) return;
            // Response header getters are stripped/incompatible in the pinned
            // game's generated Unity interop. Observe only native getters that
            // are present: result, responseCode and the downloaded text.
            var body = www.downloadHandler?.text ?? "";
            var diagnostic = ResponseDiagnostics.Read(body);
            var key = native.Pointer.ToInt64() + "|" + p.pendingId + "|" + p.probeId + "|" + diagnostic.response_sha256;
            if (p.observedHttpCompletions.Contains(key)) return;
            var folder = Path.Combine(p.root, "http-completions"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), new {
                captured_at = Now, p.session, endpoint = path, method = www.method,
                request_id = p.pendingId, probe_id = p.probeInFlight ? p.probeId : "",
                http_status = www.responseCode, result = www.result.ToString(),
                response_headers_available = false,
                diagnostic
            });
            p.observedHttpCompletions.Add(key);
            p.httpDiagnosticNote = "Native HTTP completion observation active (response headers unavailable)";
        }
        catch (Exception ex)
        {
            p.httpDiagnosticNote = "HTTP observation deferred: " + ex.GetType().Name;
            if (ex is MissingMethodException)
            {
                try
                {
                    var folder = Path.Combine(p.root, "http-observation-errors"); Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, p.session + ".json");
                    if (!File.Exists(path)) Atomic(path, new { captured_at = Now, error = ex.Message, stack = ex.StackTrace });
                }
                catch { /* Diagnostic failure cannot interrupt the native coroutine. */ }
            }
        }
    }
}
