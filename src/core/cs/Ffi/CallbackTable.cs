using System.Collections.Generic;

namespace Buckminster.Ffi;

// Userdata registry for callbacks crossing the FFI: a callback is always an [UnmanagedCallersOnly] static plus a u64 key registered here, and the static recovers its target object by key when invoked. This is the table of *userdata objects*, not of callbacks -- and it's process-wide FFI plumbing, not engine state: the static thunk arrives with nothing but the key, so the resolver must be reachable from static context, and a single key namespace needs no partition coordination. Locked because different engines may be driven concurrently from different threads (PLAN.md, multiple engines and the threading model); the critical sections are a handful of instructions, so contention is noise.
internal static class CallbackTable
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<ulong, object> Targets = new Dictionary<ulong, object>();

    // Starts at 1 so key 0 (the default(ulong) a zeroed struct or forgotten field produces) is never valid and Get catches it loudly.
    private static ulong nextKey = 1;

    internal static ulong Register(object target)
    {
        lock (Gate)
        {
            // The counter is part of the same invariant as the dictionary -- one lock scope covers read, increment, and add.
            ulong key = nextKey;
            nextKey += 1;
            Targets.Add(key, target);
            return key;
        }
    }

    internal static object Get(ulong key)
    {
        lock (Gate)
        {
            if (!Targets.TryGetValue(key, out object? target))
            {
                throw new KeyNotFoundException($"CallbackTable has no entry for key {key} -- stale, unregistered, or never-registered userdata key crossed the FFI.");
            }
            return target;
        }
    }

    internal static void Unregister(ulong key)
    {
        lock (Gate)
        {
            if (!Targets.Remove(key))
            {
                throw new KeyNotFoundException($"CallbackTable.Unregister: no entry for key {key} -- double-unregister or never-registered key.");
            }
        }
    }
}
