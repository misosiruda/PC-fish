using PCFish.Automation;
using System.Runtime.CompilerServices;

internal static class ResponseBindingTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference AddPayload(ResponseBindings<object> bindings, string id)
    {
        var payload = new object();
        bindings.Register(10,id,payload,()=>true,()=>{});
        return new WeakReference(payload);
    }

    internal static void Run(Action<bool,string> check)
    {
        using var bindings = new ResponseBindings<object>();
        bool alive = true; int releases = 0;
        var snapshot = new object();
        bindings.Register(1,"old",snapshot,()=>alive,()=>releases++);
        check(bindings.TryGet(1,out var active) && ReferenceEquals(active.Payload,snapshot),"active request retains its snapshot and callback root");
        bindings.Settle("other");
        check(bindings.ActiveCount==1,"another request cannot release an active binding");
        bindings.Settle("old");
        check(bindings.TryGet(1,out var settled) && settled.Settled && settled.Payload==null && settled.RequestId=="old",
            "settled duplicate/late callback keeps identity without snapshot or native root");
        check(releases==0,"live native target retains only its weak identity check");
        alive=false;
        check(!bindings.TryGet(1,out _) && bindings.Count==0 && releases==1,"collected native target cannot block a pointer reused by an unrelated callback");
        bindings.PruneSettled();
        check(releases==1,"weak handle is released exactly once");
        for(int i=0;i<1000;i++)
        {
            bindings.Register(i,"request-"+i,new object(),()=>true,()=>releases++);
            bindings.Settle("request-"+i);
        }
        check(bindings.ActiveCount==0 && Enumerable.Range(0,1000).All(i=>bindings.TryGet(i,out var e)&&e.Payload==null),
            "long-running completed actions retain no heavy payloads");
        bindings.Register(42,"replacement",snapshot,()=>true,()=>releases++);
        check(bindings.TryGet(42,out var replacement) && replacement.RequestId=="replacement" && releases==2,
            "new registration safely replaces a reused callback address");
        bindings.Dispose();
        check(bindings.Count==0 && releases==1002,"registry disposal frees each remaining weak lifetime handle once");
        bool expired=false;
        bindings.Register(9,"prune",new object(),()=>!expired,()=>releases++);
        bindings.Settle("prune");expired=true;bindings.PruneSettled();
        check(bindings.Count==0 && releases==1003,"periodic pruning drops collected settled callbacks");
        using var collection = new ResponseBindings<object>();
        var weak = AddPayload(collection,"pending");
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        check(weak.IsAlive,"unresolved transaction keeps its required payload alive");
        collection.Settle("pending");
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        check(!weak.IsAlive,"settled transaction payload is actually garbage collectible");
    }
}
