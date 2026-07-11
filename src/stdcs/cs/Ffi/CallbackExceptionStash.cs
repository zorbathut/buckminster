using System;

namespace Buckminster.Ffi;

// The C#-side half of surfacing callback failures across the FFI (ARCHITECTURE.md, FFI section): a callback's catch stashes its exception here and returns nonzero, Rust surfaces CallbackError to the original caller, and the caller takes the stash and rethrows. Per-thread, mirroring the Rust side's thread-local LAST_ERROR discipline; take-semantics so a stale exception can never be misattributed to a later failure.
internal static class CallbackExceptionStash
{
    [ThreadStatic]
    private static Exception? stashed;

    internal static void Stash(Exception exception)
    {
        stashed = exception;
    }

    // Null when nothing has been stashed since the last Take -- legitimate, since a callback may return nonzero for a non-exception failure.
    internal static Exception? Take()
    {
        Exception? taken = stashed;
        stashed = null;
        return taken;
    }
}
