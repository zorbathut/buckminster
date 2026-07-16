using System;
using NUnit.Framework;

namespace Buckminster.Tests;

// Engine.QueueExit / ExitQueued: the deferred exit seam. The engine itself never acts on the flag -- a module calling mid-Tick sits under the engine's own iteration, so synchronous teardown is impossible; the host loop condition is what honors it. These tests pin that division of labor as contract, not comment.
[TestFixture]
public class EngineExitTests
{
    private static Engine CreateEngine()
    {
        return Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 }, (level, message) => { });
    }

    private class ModuleQueuesExitInInit : IModule
    {
        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            engine.QueueExit();
        }

        public void PumpEvents(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    private class ModuleTickCounter : IModule
    {
        public int Ticks;

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void PumpEvents(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
            Ticks += 1;
        }
    }

    [Test]
    public void ExitQueuedDefaultsFalseAndQueueExitSetsIt()
    {
        using Engine engine = CreateEngine();
        Assert.That(engine.ExitQueued, Is.False);
        engine.QueueExit();
        Assert.That(engine.ExitQueued, Is.True);
        // Idempotent: queueing again is a no-op, not an error.
        engine.QueueExit();
        Assert.That(engine.ExitQueued, Is.True);
    }

    [Test]
    public void QueueExitIsLegalPreReady()
    {
        // The flag is just a flag, Ready or not.
        using Engine engine = CreateEngine();
        Assert.That(engine.IsReady, Is.False);
        engine.QueueExit();
        Assert.That(engine.ExitQueued, Is.True);
    }

    [Test]
    public void QueueExitFromModuleInitializeWorks()
    {
        // The claimed scenario verbatim: a module queues exit from its own Initialize, under PumpEvents' Current save-and-restore. Init still completes -- the flag never short-circuits anything engine-side.
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleQueuesExitInInit());
        engine.PumpEvents();
        Assert.That(engine.ExitQueued, Is.True);
        Assert.That(engine.IsReady, Is.True);
    }

    [Test]
    public void TickingPastExitQueuedStillRunsModulesAndCounts()
    {
        using Engine engine = CreateEngine();
        ModuleTickCounter module = new ModuleTickCounter();
        engine.RegisterModule(module);
        engine.PumpEvents();
        engine.QueueExit();
        engine.Tick(0.016);
        engine.Tick(0.016);
        // The engine never acts on the flag: honoring it is host policy, so ticking past it is fully functional.
        Assert.That(module.Ticks, Is.EqualTo(2));
        Assert.That(engine.TickCount, Is.EqualTo(2));
    }

    [Test]
    public void QueueExitThrowsOnDisposedButExitQueuedStaysReadable()
    {
        Engine engine = CreateEngine();
        engine.QueueExit();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(engine.QueueExit);
        // The read stays guard-free like IsReady/TickCount: a host wrapper checking the flag after teardown is a plausible consumer.
        Assert.That(engine.ExitQueued, Is.True);
    }
}
