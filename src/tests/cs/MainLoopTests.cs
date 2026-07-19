using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// MainLoop's fixed-step pacing, tested through the internal deterministic entry (IterateWithElapsed) -- the public Iterate() only adds the monotonic clock read, and wall-clock in tests is banned. Pinned semantics: the accumulator starts at the Ready latch (init time never converts into a tick burst; sim tick 0 stays pinned to Ready), a burst stops at ExitQueued mid-step, and the step clamp bounds any single iterate.
[TestFixture]
public class MainLoopTests
{
    private static readonly EngineConfig Config = new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 };

    [TearDown]
    public void TearDown()
    {
        // Manual cycles here (pacing needs precise control of when Ready latches); idempotent Shutdown stops a mid-cycle assertion failure from cascading into later tests.
        Engine.Shutdown();
    }

    private class ModuleCounter : IModule
    {
        public int Ticks;
        public int Pumps;
        public readonly List<double> Dts = new List<double>();

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
            Pumps += 1;
        }

        public void Tick(double dt)
        {
            Ticks += 1;
            Dts.Add(dt);
        }
    }

    private class ModuleQueuesExitOnFirstTick : IModule
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
            Engine.QueueExit();
        }
    }

    [Test]
    public void PreReadyElapsedIsDiscardedAndAccumulationIsFixedStep()
    {
        ModuleCounter module = new ModuleCounter();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { module });
        double step = MainLoop.FixedStep;
        // First iterate latches Ready (init pump); its elapsed is init time, not sim time -- a huge value here must convert into ZERO ticks.
        MainLoop.IterateWithElapsed(step * 1000.0);
        Assert.That(Engine.IsReady, Is.True);
        Assert.That(module.Ticks, Is.Zero);
        // 2.5 steps of elapsed: two ticks, half a step carried.
        MainLoop.IterateWithElapsed(step * 2.5);
        Assert.That(module.Ticks, Is.EqualTo(2));
        // 0.6 more: the carried 0.5 pushes it over one step.
        MainLoop.IterateWithElapsed(step * 0.6);
        Assert.That(module.Ticks, Is.EqualTo(3));
        // Under threshold: no tick.
        MainLoop.IterateWithElapsed(step * 0.05);
        Assert.That(module.Ticks, Is.EqualTo(3));
        Assert.That(Engine.TickCount, Is.EqualTo(3));
        // Every tick received exactly the fixed step as its dt, never the raw elapsed.
        Assert.That(module.Dts, Is.All.EqualTo(step));
        Engine.Shutdown();
    }

    [Test]
    public void FixedStepRejectsNonpositiveValues()
    {
        Engine.Initialize(Config, (level, message) => { }, Array.Empty<IModule>());
        Assert.Throws<ArgumentOutOfRangeException>(() => MainLoop.FixedStep = 0.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => MainLoop.FixedStep = -1.0);
        Engine.Shutdown();
    }

    [Test]
    public void StepClampBoundsASingleIterateAndDropsTheExcess()
    {
        ModuleCounter module = new ModuleCounter();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { module });
        MainLoop.IterateWithElapsed(0.0);
        // A giant stall (or debugger pause) must not spiral: one iterate runs at most MaxStepsPerIterate ticks, and the un-run excess is DROPPED, not carried into the next iterate.
        MainLoop.IterateWithElapsed(MainLoop.FixedStep * 100.0);
        Assert.That(module.Ticks, Is.EqualTo(MainLoop.MaxStepsPerIterate));
        MainLoop.IterateWithElapsed(MainLoop.FixedStep * 0.9);
        Assert.That(module.Ticks, Is.EqualTo(MainLoop.MaxStepsPerIterate));
        Engine.Shutdown();
    }

    [Test]
    public void ExitQueuedStopsTheBurstMidIterate()
    {
        // The smoke-sentinel guarantee: a module that queues exit during a tick must end the burst THERE -- a CI stall around the exit tick must not produce extra ticks past it.
        ModuleQueuesExitOnFirstTick module = new ModuleQueuesExitOnFirstTick();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { module });
        MainLoop.IterateWithElapsed(0.0);
        MainLoop.IterateWithElapsed(MainLoop.FixedStep * 3.0);
        Assert.That(module.Ticks, Is.EqualTo(1));
        Assert.That(Engine.TickCount, Is.EqualTo(1));
        Engine.Shutdown();
    }

    [Test]
    public void PreReadyIterateRunsTheInitPumpOnly()
    {
        ModuleCounter module = new ModuleCounter();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { module });
        // The init pump runs no module pumps (the IModule contract); post-Ready iterates run one module pump each.
        MainLoop.IterateWithElapsed(0.0);
        Assert.That(module.Pumps, Is.Zero);
        MainLoop.IterateWithElapsed(0.0);
        Assert.That(module.Pumps, Is.EqualTo(1));
        Engine.Shutdown();
    }
}
