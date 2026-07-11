using System;
using System.Runtime.InteropServices;

namespace Buckminster.Ffi;

// The M2 walking-skeleton smoke test: the M1 FFI round trip (add, callback via key table, panic containment) condensed into lines the wasm hosts print/render. Every expectation is checked and throws on mismatch -- a broken link must be a loud failure, never a wrong line. Dies at M3 when the in-host test runner runs the real suite instead.
internal static class FfiSmoke
{
    private sealed class SmokeTarget
    {
        public int ObservedValue;
    }

    [UnmanagedCallersOnly]
    private static unsafe int SmokeCallback(ulong userdata, int value, int* outResult)
    {
        // Canonical callback body: no exception may ever escape (PLAN.md callback rule 2).
        try
        {
            SmokeTarget target = (SmokeTarget)CallbackTable.Get(userdata);
            target.ObservedValue = value;
            *outResult = value * 2;
            return 0;
        }
        catch (Exception e)
        {
            CallbackExceptionStash.Stash(e);
            return 1;
        }
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FFI smoke test failed: {what} (last error: {NativeMethods.LastErrorMessage() ?? "none"})");
        }
    }

    internal static unsafe string[] Run()
    {
        FfiCode addCode = NativeMethods.buck_add(2, 3, out int sum);
        Expect(addCode == FfiCode.Ok && sum == 5, $"buck_add(2, 3) returned code {addCode}, sum {sum}");

        SmokeTarget target = new SmokeTarget();
        ulong key = CallbackTable.Register(target);
        FfiCode callbackCode = NativeMethods.buck_callback_invoke((IntPtr)(delegate* unmanaged<ulong, int, int*, int>)&SmokeCallback, key, 21, out int result);
        CallbackTable.Unregister(key);
        Exception? stashed = CallbackExceptionStash.Take();
        if (callbackCode != FfiCode.Ok && stashed != null)
        {
            // The stash carries the actual managed exception from inside the callback -- the single most valuable diagnostic when the round trip breaks (e.g. a mangled userdata key throwing in CallbackTable.Get).
            throw new InvalidOperationException($"FFI smoke test failed: callback reported an exception (outer code {callbackCode})", stashed);
        }
        Expect(callbackCode == FfiCode.Ok && result == 42, $"buck_callback_invoke returned code {callbackCode}, result {result}");
        Expect(target.ObservedValue == 21, $"callback target observed {target.ObservedValue}, expected 21 via the key table");

        FfiCode panicCode = NativeMethods.buck_test_panic();
        Expect(panicCode == FfiCode.Panic, $"buck_test_panic returned code {panicCode}");
        string? panicMessage = NativeMethods.LastErrorMessage();
        Expect(panicMessage != null && panicMessage.Contains("deliberate panic"), $"panic message was '{panicMessage}'");
        FfiCode afterPanic = NativeMethods.buck_add(1, 1, out int two);
        Expect(afterPanic == FfiCode.Ok && two == 2, "buck_add after contained panic");

        return new string[]
        {
            "buck_add(2, 3) = 5",
            "callback round trip: 21 -> 42, observed through the key table",
            "panic contained: code Panic, message intact, runtime alive",
        };
    }
}
