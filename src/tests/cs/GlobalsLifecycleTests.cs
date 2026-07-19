using Buckminster;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

// The raw FFI globals lifecycle (M5.75 ABI split): init/tick/shutdown cycles and the GLOBAL poison-on-panic latch, whose recovery path is re-initialization -- the Rust half of the C# wedge-then-Shutdown-then-Initialize flow. Raw calls, no EngineScope: these tests own the Rust globals directly (the suite is sequential, and the C# facade's static state is untouched).
[TestFixture]
public class GlobalsLifecycleTests
{
    private static EngineConfig DefaultConfig()
    {
        return new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 };
    }

    [Test]
    public void InitTickShutdownCycleAndReinitResets()
    {
        Assert.That(NativeMethods.buck_globals_init(DefaultConfig()), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_tick(0.016, out ulong first), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_tick(0.016, out ulong second), Is.EqualTo(FfiCode.Ok));
        Assert.That(first, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(2));
        // Re-init without shutdown is a reset at this layer (the C# facade guards double-init; Rust is last-wins like the logging config it carries).
        Assert.That(NativeMethods.buck_globals_init(DefaultConfig()), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_tick(0.016, out ulong reset), Is.EqualTo(FfiCode.Ok));
        Assert.That(reset, Is.EqualTo(1));
        Assert.That(NativeMethods.buck_globals_shutdown(), Is.EqualTo(FfiCode.Ok));
        // Ticking a shut-down globals is a loud protocol error, and shutdown itself is idempotent.
        Assert.That(NativeMethods.buck_globals_tick(0.016, out _), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("not initialized"));
        Assert.That(NativeMethods.buck_globals_shutdown(), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void PanicPoisonsTheGlobalsAndReinitRecovers()
    {
        Assert.That(NativeMethods.buck_globals_init(DefaultConfig()), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_test_panic(), Is.EqualTo(FfiCode.Panic));
        // The globals-scoped inner catch is a different path from guard's outer one -- pin that the original panic text survives it.
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("deliberate panic"));
        Assert.That(NativeMethods.buck_globals_tick(0.016, out _), Is.EqualTo(FfiCode.Poisoned));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("poisoned"));
        // Re-init WITHOUT a shutdown also clears the latch (init is the documented reset at this layer)...
        Assert.That(NativeMethods.buck_globals_init(DefaultConfig()), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_test_panic(), Is.EqualTo(FfiCode.Panic));
        // ...and shutdown works on poisoned globals (teardown is a guarantee), with a fresh init clearing the latch again.
        Assert.That(NativeMethods.buck_globals_shutdown(), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_init(DefaultConfig()), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_globals_tick(0.016, out ulong count), Is.EqualTo(FfiCode.Ok));
        Assert.That(count, Is.EqualTo(1));
        Assert.That(NativeMethods.buck_globals_shutdown(), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void ZeroLogBufferCapacityIsRejected()
    {
        EngineConfig config = new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 0 };
        Assert.That(NativeMethods.buck_globals_init(config), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("log_buffer_capacity"));
    }
}
