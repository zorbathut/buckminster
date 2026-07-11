using System.Collections.Generic;

namespace Buckminster.Ffi;

// Userdata registry for callbacks crossing the FFI: a callback is always an [UnmanagedCallersOnly] static plus a u64 key registered here, and the static recovers its target object by key when invoked. This is the table of *userdata objects*, not of callbacks. Not locked -- everything is single-threaded until profiling says otherwise (PLAN.md's threading entry).
internal static class CallbackTable
{
    private static readonly Dictionary<ulong, object> Targets = new Dictionary<ulong, object>();

    // Starts at 1 so key 0 (the default(ulong) a zeroed struct or forgotten field produces) is never valid and Get catches it loudly.
    private static ulong nextKey = 1;

    internal static ulong Register(object target)
    {
        ulong key = nextKey;
        nextKey += 1;
        Targets.Add(key, target);
        return key;
    }

    internal static object Get(ulong key)
    {
        if (!Targets.TryGetValue(key, out object? target))
        {
            throw new KeyNotFoundException($"CallbackTable has no entry for key {key} -- stale, unregistered, or never-registered userdata key crossed the FFI.");
        }
        return target;
    }

    internal static void Unregister(ulong key)
    {
        if (!Targets.Remove(key))
        {
            throw new KeyNotFoundException($"CallbackTable.Unregister: no entry for key {key} -- double-unregister or never-registered key.");
        }
    }
}
