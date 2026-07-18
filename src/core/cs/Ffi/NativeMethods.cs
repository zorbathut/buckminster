using System;
using System.Runtime.InteropServices;

namespace Buckminster.Ffi;

// The hand-written residue of the raw import layer: exactly buck_last_error_message (deliberately guard-less -- see the Rust side) plus the Library constant the generated partial shares. Everything #[buck_export]-converted lives in the generated partial (Generated/NativeMethods.g.cs). Convention unchanged: native snake_case names, FfiCode returns, out-params unspecified on a nonzero return.
internal static partial class NativeMethods
{
    private const string Library = "buckminster_core";

    [LibraryImport(Library)]
    private static partial IntPtr buck_last_error_message();

    // Null when the last buck_* call on this thread succeeded. The native pointer is only valid until the next buck_* call on this thread (reading via buck_last_error_message itself is non-destructive), so copy to a managed string immediately.
    internal static string? LastErrorMessage()
    {
        return Marshal.PtrToStringUTF8(buck_last_error_message());
    }
}
