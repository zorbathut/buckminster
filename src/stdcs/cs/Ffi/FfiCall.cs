using System;
using System.Runtime.ExceptionServices;

namespace Buckminster.Ffi;

// The one error-surfacing path for FFI results. The stash is taken UNCONDITIONALLY on any failure: a log-sink exception raised during a failed call's exit-drain must never be orphaned in the stash (where a later CallbackError would misattribute it) -- if the body's own error wins the return code, the sink's exception rides along as InnerException.
internal static class FfiCall
{
    internal static void ThrowOnError(FfiCode code, string what)
    {
        if (code == FfiCode.Ok)
        {
            return;
        }
        Exception? stashed = CallbackExceptionStash.Take();
        if (code == FfiCode.CallbackError && stashed != null)
        {
            // The failure IS the callback's exception (e.g. the log sink threw while the body succeeded); rethrow it at the causal call site with its original stack.
            ExceptionDispatchInfo.Capture(stashed).Throw();
        }
        string message = NativeMethods.LastErrorMessage() ?? "(no error message recorded)";
        throw new InvalidOperationException($"{what} failed with {code}: {message}", stashed);
    }
}
