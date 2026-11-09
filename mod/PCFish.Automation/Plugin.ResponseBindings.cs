using Il2CppInterop.Runtime;
using NN.PF.Core.Managers;

namespace PCFish.Automation;

public sealed partial class Plugin
{
    void SettleResponseBindings(string requestId)
    {
        responseBindings.Settle(requestId);
        if (dispatchedCommand?.request_id != requestId) return;
        dispatchedCommand = null; dispatchBefore = null; callback = null;
    }

    sealed class NativeResponseBinding(Command command, Snapshot before, string account, Il2CppSystem.Object target)
    {
        internal Command command = command;
        internal Snapshot before = before;
        internal string account = account;
        internal Il2CppSystem.Object target = target;
    }

    sealed class NativeResponseLifetime
    {
        readonly IntPtr pointer;
        readonly IntPtr handle;
        bool disposed;
        internal NativeResponseLifetime(IntPtr pointer)
        {
            this.pointer = pointer;
            handle = IL2CPP.il2cpp_gchandle_new_weakref(pointer, false);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Native callback weak lifetime could not be established");
        }
        internal bool Alive() => !disposed && IL2CPP.il2cpp_gchandle_get_target(handle) == pointer;
        internal void Release()
        {
            if (disposed) return;
            disposed = true; IL2CPP.il2cpp_gchandle_free(handle);
        }
    }

    void ObserveSettledResponse(string requestId, string response)
    {
        var diagnostic = ResponseDiagnostics.Read(response);
        if (diagnostic.status == "success" && !observedChildReceipts.Contains(diagnostic.child_uid))
        {
            inventorySynced = false;
            inventorySyncNote = "Late response retained for authoritative reconciliation";
        }
        try
        {
            var folder = Path.Combine(root, "response-callback-events"); Directory.CreateDirectory(folder);
            Atomic(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), new {
                request_id = requestId, captured_at = Now, diagnostic.status, diagnostic.child_uid,
                current_request = false, settled_request = true,
                duplicate_child_receipt = observedChildReceipts.Contains(diagnostic.child_uid) });
        }
        catch (Exception ex) { responseDiagnosticNote = "Settled callback diagnostic deferred: " + ex.GetType().Name; }
    }
}
