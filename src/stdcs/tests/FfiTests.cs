using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

[TestFixture]
public class FfiTests
{
    private sealed class CallbackTarget
    {
        public int ObservedValue;
    }

    [UnmanagedCallersOnly]
    private static unsafe int CallbackDouble(ulong userdata, int value, int* outResult)
    {
        // Canonical callback body: no exception may ever escape (PLAN.md callback rule 2) -- catch everything, stash for the caller, report via the return code.
        try
        {
            CallbackTarget target = (CallbackTarget)CallbackTable.Get(userdata);
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

    [UnmanagedCallersOnly]
    private static unsafe int CallbackThrows(ulong userdata, int value, int* outResult)
    {
        try
        {
            throw new InvalidOperationException("deliberate exception for FFI containment testing");
        }
        catch (Exception e)
        {
            CallbackExceptionStash.Stash(e);
            return 1;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int CallbackReenters(ulong userdata, int value, int* outResult)
    {
        try
        {
            FfiCode code = NativeMethods.buck_add(value, 10, out int sum);
            if (code != FfiCode.Ok)
            {
                return (int)code;
            }
            *outResult = sum;
            return 0;
        }
        catch (Exception e)
        {
            CallbackExceptionStash.Stash(e);
            return 1;
        }
    }

    [Test]
    public void AddReturnsSum()
    {
        FfiCode code = NativeMethods.buck_add(2, 3, out int sum);
        Assert.That(code, Is.EqualTo(FfiCode.Ok));
        Assert.That(sum, Is.EqualTo(5));
        Assert.That(NativeMethods.LastErrorMessage(), Is.Null);
    }

    [Test]
    public void AddOverflowReturnsError()
    {
        FfiCode code = NativeMethods.buck_add(int.MaxValue, 1, out _);
        Assert.That(code, Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("overflow"));
    }

    [Test]
    public void PanicIsContained()
    {
        FfiCode code = NativeMethods.buck_test_panic();
        Assert.That(code, Is.EqualTo(FfiCode.Panic));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("deliberate panic"));

        // The runtime survived the contained panic: a subsequent call works normally.
        FfiCode addCode = NativeMethods.buck_add(1, 1, out int sum);
        Assert.That(addCode, Is.EqualTo(FfiCode.Ok));
        Assert.That(sum, Is.EqualTo(2));
    }

    [Test]
    public void LastErrorClearsOnSuccess()
    {
        NativeMethods.buck_test_panic();
        Assert.That(NativeMethods.LastErrorMessage(), Is.Not.Null);
        NativeMethods.buck_add(1, 2, out _);
        Assert.That(NativeMethods.LastErrorMessage(), Is.Null);
    }

    [Test]
    public void LastErrorSurvivesReads()
    {
        // Pins the non-destructive-read carve-out: buck_last_error_message must not route through the guard (which clears on success) or the first read would destroy the error.
        NativeMethods.buck_test_panic();
        string? first = NativeMethods.LastErrorMessage();
        string? second = NativeMethods.LastErrorMessage();
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public unsafe void CallbackRoundTrip()
    {
        CallbackTarget target = new CallbackTarget();
        ulong key = CallbackTable.Register(target);
        FfiCode code = NativeMethods.buck_callback_invoke(&CallbackDouble, key, 21, out int result);
        CallbackTable.Unregister(key);

        Assert.That(code, Is.EqualTo(FfiCode.Ok));
        Assert.That(result, Is.EqualTo(42));
        // The key-table-recovery proof: the instance registered up here observed the value down in the callback.
        Assert.That(target.ObservedValue, Is.EqualTo(21));
    }

    [Test]
    public unsafe void CallbackExceptionIsContained()
    {
        CallbackExceptionStash.Take();
        FfiCode code = NativeMethods.buck_callback_invoke(&CallbackThrows, 0, 5, out _);

        Assert.That(code, Is.EqualTo(FfiCode.CallbackError));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("callback returned error code 1"));
        Exception? caught = CallbackExceptionStash.Take();
        Assert.That(caught, Is.InstanceOf<InvalidOperationException>());
        // Take-semantics: consuming the stash empties it, so a stale exception can't be misattributed later.
        Assert.That(CallbackExceptionStash.Take(), Is.Null);

        // The runtime survived the contained exception: a subsequent call works normally.
        FfiCode addCode = NativeMethods.buck_add(2, 2, out int sum);
        Assert.That(addCode, Is.EqualTo(FfiCode.Ok));
        Assert.That(sum, Is.EqualTo(4));
    }

    [Test]
    public unsafe void CallbackReentersFfi()
    {
        // Pins guard nesting: a callback calling back into buck_* on the same thread is the normal future state (PLAN.md: callbacks used freely), not an edge case. The pre-existing error proves nested success handles LAST_ERROR sanely.
        NativeMethods.buck_test_panic();
        FfiCode code = NativeMethods.buck_callback_invoke(&CallbackReenters, 0, 5, out int result);

        Assert.That(code, Is.EqualTo(FfiCode.Ok));
        Assert.That(result, Is.EqualTo(15));
        Assert.That(NativeMethods.LastErrorMessage(), Is.Null);
    }

    [Test]
    public void CallbackTableGetMissingThrows()
    {
        Assert.That(() => CallbackTable.Get(0), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(() => CallbackTable.Get(ulong.MaxValue), Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void CallbackTableUnregister()
    {
        ulong key = CallbackTable.Register(new object());
        CallbackTable.Unregister(key);
        Assert.That(() => CallbackTable.Get(key), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(() => CallbackTable.Unregister(key), Throws.TypeOf<KeyNotFoundException>());
    }
}
