using System;
using System.Runtime.InteropServices;

namespace Buckminster.Ffi;

// Hand-written bindings to the buckminster-core cdylib. Imports keep the native snake_case names for 1:1 greppability against the Rust exports in src/core/rust/lib.rs; the friendly PascalCase API layer arrives with M4's Engine, not here. Convention: every fallible export returns an FfiCode (blittable, ABI-identical to the native i32) and writes results through out-params -- which are unspecified on a nonzero return, so check the code first.
internal static partial class NativeMethods
{
    private const string Library = "buckminster_core";

    [LibraryImport(Library)]
    internal static partial FfiCode buck_add(int a, int b, out int sum);

    [LibraryImport(Library)]
    internal static unsafe partial FfiCode buck_callback_invoke(delegate* unmanaged<ulong, int, int*, int> callback, ulong userdata, int value, out int result);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_test_panic();

    [LibraryImport(Library)]
    private static partial IntPtr buck_last_error_message();

    // Null when the last buck_* call on this thread succeeded. The native pointer is only valid until the next buck_* call on this thread (reading via buck_last_error_message itself is non-destructive), so copy to a managed string immediately.
    internal static string? LastErrorMessage()
    {
        return Marshal.PtrToStringUTF8(buck_last_error_message());
    }
}
