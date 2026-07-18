using Buckminster;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

// The raw FFI lifecycle: create/destroy, handle staleness, and the poison-on-panic policy (a caught panic marks the engine suspect; everything but destroy then fails with EnginePoisoned). The friendly C# Engine class layers on top of these in the next M4 chunk.
[TestFixture]
public class EngineLifecycleTests
{
    private static EngineConfig DefaultConfig()
    {
        return new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 };
    }

    [Test]
    public void CreateDestroyRoundtrip()
    {
        FfiCode code = NativeMethods.buck_engine_create(DefaultConfig(), out ulong engine);
        Assert.That(code, Is.EqualTo(FfiCode.Ok));
        Assert.That(engine, Is.Not.Zero);
        Assert.That(NativeMethods.buck_engine_destroy(engine), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void DestroyTwiceIsInvalidArgument()
    {
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong engine);
        Assert.That(NativeMethods.buck_engine_destroy(engine), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_engine_destroy(engine), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.LastErrorMessage(), Is.Not.Null);
    }

    [Test]
    public void NullHandleIsInvalidArgument()
    {
        Assert.That(NativeMethods.buck_engine_destroy(0), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.buck_engine_tick(0, 0.016, out _), Is.EqualTo(FfiCode.InvalidArgument));
    }

    [Test]
    public void TickCountsFromOne()
    {
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong engine);
        Assert.That(NativeMethods.buck_engine_tick(engine, 0.016, out ulong first), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_engine_tick(engine, 0.016, out ulong second), Is.EqualTo(FfiCode.Ok));
        Assert.That(first, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(2));
        NativeMethods.buck_engine_destroy(engine);
    }

    [Test]
    public void TickAfterDestroyIsInvalidArgument()
    {
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong engine);
        NativeMethods.buck_engine_destroy(engine);
        Assert.That(NativeMethods.buck_engine_tick(engine, 0.016, out _), Is.EqualTo(FfiCode.InvalidArgument));
    }

    [Test]
    public void StaleHandleAfterSlotReuseIsInvalidArgument()
    {
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong first);
        NativeMethods.buck_engine_destroy(first);
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong second);
        // The slot got reused (LIFO free list) but the generation moved on -- the old handle must not alias the new engine.
        Assert.That(NativeMethods.buck_engine_tick(first, 0.016, out _), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.buck_engine_tick(second, 0.016, out _), Is.EqualTo(FfiCode.Ok));
        NativeMethods.buck_engine_destroy(second);
    }

    [Test]
    public void PanicPoisonsTheEngineButDestroyStillWorks()
    {
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong engine);
        Assert.That(NativeMethods.buck_engine_test_panic(engine), Is.EqualTo(FfiCode.Panic));
        // The engine-scoped inner catch is a different path from guard's outer one -- pin that the original panic text survives it.
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("deliberate panic"));
        Assert.That(NativeMethods.buck_engine_tick(engine, 0.016, out _), Is.EqualTo(FfiCode.EnginePoisoned));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("poisoned"));
        Assert.That(NativeMethods.buck_engine_destroy(engine), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void PoisonIsPerEngineNotProcess()
    {
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong poisonedEngine);
        NativeMethods.buck_engine_create(DefaultConfig(), out ulong healthyEngine);
        NativeMethods.buck_engine_test_panic(poisonedEngine);
        // The sibling engine and fresh creation must be untouched -- the panic poisons one engine, not the registry.
        Assert.That(NativeMethods.buck_engine_tick(healthyEngine, 0.016, out _), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_engine_create(DefaultConfig(), out ulong thirdEngine), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_engine_destroy(poisonedEngine), Is.EqualTo(FfiCode.Ok));
        NativeMethods.buck_engine_destroy(healthyEngine);
        NativeMethods.buck_engine_destroy(thirdEngine);
    }

    [Test]
    public void ZeroLogBufferCapacityIsRejected()
    {
        EngineConfig config = new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 0 };
        Assert.That(NativeMethods.buck_engine_create(config, out _), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("log_buffer_capacity"));
    }

}
