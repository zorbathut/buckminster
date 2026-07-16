using System;
using System.Runtime.InteropServices;

namespace Buckminster.Ffi;

// Hand-written bindings to the buckminster-core cdylib. Imports keep the native snake_case names for 1:1 greppability against the Rust exports in src/core/rust/lib.rs; the friendly PascalCase API layer arrives with M4's Engine, not here. Convention: every fallible export returns an FfiCode (blittable, ABI-identical to the native i32) and writes results through out-params -- which are unspecified on a nonzero return, so check the code first.
internal static partial class NativeMethods
{
    private const string Library = "buckminster_core";

    [LibraryImport(Library)]
    internal static partial FfiCode buck_add(int a, int b, out int sum);

    // The callback crosses as IntPtr, not delegate* unmanaged<ulong, int, int*, int>: mono's wasm interp-to-native path cannot map function-pointer parameter types (type_to_c in aot-runtime-wasm.c aborts on them -- the dotnet/runtime #56145 class), and the two representations are ABI-identical. Call sites cast through the full delegate type -- (IntPtr)(delegate* unmanaged<ulong, int, int*, int>)&TheCallback -- deliberately: that re-asserts the callback signature at every call site, so a signature drift is a compile error instead of a runtime trap.
    [LibraryImport(Library)]
    internal static partial FfiCode buck_callback_invoke(IntPtr callback, ulong userdata, int value, out int result);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_test_panic();

    [LibraryImport(Library)]
    internal static partial FfiCode buck_engine_create(in EngineConfig config, out ulong engine);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_engine_destroy(ulong engine);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_engine_tick(ulong engine, double dt, out ulong tickCount);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_engine_test_panic(ulong engine);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_engine_test_log_then_panic(ulong engine);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_layout_engine_config(out ulong size, out ulong offsetLogLevelMax, out ulong offsetLogBufferCapacity, out ulong offsetLogStderrLevelMax);

    // The log sink crosses as IntPtr (the standing wasm binding constraint); call sites cast through delegate* unmanaged<ulong, int, byte*, nuint, int>.
    [LibraryImport(Library)]
    internal static partial FfiCode buck_log_sink_set(IntPtr logSink, ulong userdata);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_log_sink_clear();

    [LibraryImport(Library)]
    private static unsafe partial FfiCode buck_log(int level, byte* message, nuint length);

    // Friendly wrapper over the span-pair convention (native strings cross as pointer + length, never marshalled). The Log facade's other half.
    internal static unsafe FfiCode EmitLog(int level, string message)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);
        fixed (byte* pointer = bytes)
        {
            return buck_log(level, pointer, (nuint)bytes.Length);
        }
    }

    [LibraryImport(Library)]
    internal static partial FfiCode buck_platform_pump();

    [LibraryImport(Library)]
    internal static unsafe partial FfiCode buck_platform_events_poll(PlatformEventRaw* buf, uint cap, out uint written, out uint remaining);

    [LibraryImport(Library)]
    private static unsafe partial FfiCode buck_window_create(byte* titlePtr, nuint titleLen, uint width, uint height, out ulong window);

    // Friendly wrapper over the span-pair convention, same shape as EmitLog.
    internal static unsafe FfiCode WindowCreate(string title, uint width, uint height, out ulong window)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(title);
        fixed (byte* pointer = bytes)
        {
            return buck_window_create(pointer, (nuint)bytes.Length, width, height, out window);
        }
    }

    [LibraryImport(Library)]
    internal static partial FfiCode buck_window_destroy(ulong window);

    [LibraryImport(Library)]
    private static unsafe partial FfiCode buck_window_set_title(ulong window, byte* titlePtr, nuint titleLen);

    internal static unsafe FfiCode WindowSetTitle(ulong window, string title)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(title);
        fixed (byte* pointer = bytes)
        {
            return buck_window_set_title(window, pointer, (nuint)bytes.Length);
        }
    }

    [LibraryImport(Library)]
    internal static partial FfiCode buck_window_size(ulong window, out uint width, out uint height);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_layout_platform_event(out uint size, out uint offsetWindow, out uint offsetKind, out uint offsetData0, out uint offsetData1, out uint offsetData2);

    [LibraryImport(Library)]
    internal static unsafe partial FfiCode buck_test_keycode_name(uint value, byte* buf, nuint cap, out nuint length);

    [LibraryImport(Library)]
    internal static partial FfiCode buck_test_keycode_count(out uint count);

    [LibraryImport(Library)]
    private static partial IntPtr buck_last_error_message();

    // Null when the last buck_* call on this thread succeeded. The native pointer is only valid until the next buck_* call on this thread (reading via buck_last_error_message itself is non-destructive), so copy to a managed string immediately.
    internal static string? LastErrorMessage()
    {
        return Marshal.PtrToStringUTF8(buck_last_error_message());
    }
}
