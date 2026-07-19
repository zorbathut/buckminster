using System;
using NUnit.Framework;

namespace Buckminster.Tests;

// Engine.QueueExit / ExitQueued: the deferred exit seam. The engine itself never acts on the flag -- a module calling mid-Tick sits under the engine's own iteration, so synchronous teardown is impossible; the host loop condition is what honors it (and MainLoop's mid-burst check, pinned in MainLoopTests, is pacing policy layered on top). These tests pin that division of labor as contract, not comment. The after-Shutdown throw and post-Shutdown readability live in EngineGlobalsTests with the rest of the static lifecycle.
[TestFixture]
public class EngineExitTests
{
    private class ModuleQueuesExitInInit : IModule
    {
        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize()
        {
            Engine.QueueExit();
        }

        public void Shutdown()
        {
        }

        public void PumpEvents()
        {
        }

        public void Tick(double dt)
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

        public void Initialize()
        {
        }

        public void Shutdown()
        {
        }

        public void PumpEvents()
        {
        }

        public void Tick(double dt)
        {
            Ticks += 1;
        }
    }

    [Test]
    public void ExitQueuedDefaultsFalseAndQueueExitSetsIt()
    {
        using EngineScope scope = new EngineScope();
        Assert.That(Engine.ExitQueued, Is.False);
        Engine.QueueExit();
        Assert.That(Engine.ExitQueued, Is.True);
        // Idempotent: queueing again is a no-op, not an error.
        Engine.QueueExit();
        Assert.That(Engine.ExitQueued, Is.True);
    }

    [Test]
    public void QueueExitIsLegalPreReady()
    {
        // The flag is just a flag, Ready or not.
        using EngineScope scope = new EngineScope();
        Assert.That(Engine.IsReady, Is.False);
        Engine.QueueExit();
        Assert.That(Engine.ExitQueued, Is.True);
    }

    [Test]
    public void QueueExitFromModuleInitializeWorks()
    {
        // The claimed scenario verbatim: a module queues exit from its own Initialize. Init still completes -- the flag never short-circuits anything engine-side.
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleQueuesExitInInit() });
        Engine.PumpEvents();
        Assert.That(Engine.ExitQueued, Is.True);
        Assert.That(Engine.IsReady, Is.True);
    }

    [Test]
    public void TickingPastExitQueuedStillRunsModulesAndCounts()
    {
        ModuleTickCounter module = new ModuleTickCounter();
        using EngineScope scope = new EngineScope(modules: new IModule[] { module });
        Engine.PumpEvents();
        Engine.QueueExit();
        Engine.Tick(0.016);
        Engine.Tick(0.016);
        // The engine never acts on the flag: honoring it is host policy, so ticking past it is fully functional. (MainLoop chooses not to -- that's ITS policy, not the engine's.)
        Assert.That(module.Ticks, Is.EqualTo(2));
        Assert.That(Engine.TickCount, Is.EqualTo(2));
    }
}
