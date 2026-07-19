using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// The static Engine globals lifecycle (M5.75): Initialize(config, sink, modules) / Shutdown, cyclable within one process -- Initialize returns every piece of static state to virgin, which is what lets each test own a full cycle. Shutdown runs module Shutdown in reverse recorded init order, then tears down native state; post-Shutdown reads stay valid until the next Initialize.
[TestFixture]
public class EngineGlobalsTests
{
    private static readonly EngineConfig Config = new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 };

    [TearDown]
    public void TearDown()
    {
        // These tests manage cycles by hand (they ARE the lifecycle tests); Shutdown is idempotent, so this stops a genuine failure mid-cycle from wedging every later test into an "already initialized" cascade.
        Engine.Shutdown();
    }

    private class ModuleJournal : IModule
    {
        private readonly List<string> journal;
        private readonly string name;
        private readonly Type[] dependencies;

        public ModuleJournal(List<string> journal, string name, params Type[] dependencies)
        {
            this.journal = journal;
            this.name = name;
            this.dependencies = dependencies;
        }

        public Type[] Dependencies
        {
            get { return dependencies; }
        }

        public void Initialize()
        {
            journal.Add($"init {name}");
        }

        public void Shutdown()
        {
            journal.Add($"shutdown {name}");
        }

        public void PumpEvents()
        {
        }

        public void Tick(double dt)
        {
            journal.Add($"tick {name}");
        }
    }

    // Distinct concrete types for dependency declarations (the registry keys by concrete type).
    private class ModuleAlpha : ModuleJournal
    {
        public ModuleAlpha(List<string> journal) : base(journal, "alpha")
        {
        }
    }

    private class ModuleBravo : ModuleJournal
    {
        public ModuleBravo(List<string> journal) : base(journal, "bravo", typeof(ModuleAlpha))
        {
        }
    }

    private class ModuleObservesLifecycle : IModule
    {
        public readonly List<LifecycleEvent> Observed = new List<LifecycleEvent>();

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize()
        {
            Engine.Lifecycle += Observed.Add;
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

    [Test]
    public void TwoFullCyclesLeaveNoBleedThrough()
    {
        // Cycle 1: dirty every piece of static state -- ticks, exit flag, a lifecycle subscriber, ready latch.
        List<string> journal = new List<string>();
        List<LifecycleEvent> firstCycleObserved = new List<LifecycleEvent>();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { new ModuleAlpha(journal) });
        Engine.Lifecycle += firstCycleObserved.Add;
        MainLoop.FixedStep = 1.0 / 30.0;
        Engine.PumpEvents();
        Engine.Tick(0.016);
        Engine.QueueExit();
        Assert.That(Engine.IsReady, Is.True);
        Assert.That(Engine.TickCount, Is.EqualTo(1));
        Assert.That(Engine.ExitQueued, Is.True);
        Engine.Shutdown();

        // Cycle 2: Initialize must present virgin state -- and the cycle-1 subscriber must be gone (ghost delegates across cycles are the named hazard).
        List<string> secondJournal = new List<string>();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { new ModuleAlpha(secondJournal) });
        Assert.That(Engine.IsReady, Is.False);
        Assert.That(Engine.TickCount, Is.Zero);
        Assert.That(Engine.ExitQueued, Is.False);
        Engine.PumpEvents();
        Engine.NotifyLifecycle(LifecycleEvent.Suspended);
        Assert.That(firstCycleObserved, Is.Empty);
        Engine.Tick(0.016);
        Assert.That(Engine.TickCount, Is.EqualTo(1));
        // MainLoop's pacing knob is cycle state too: cycle 1's custom value must not leak into cycle 2.
        Assert.That(MainLoop.FixedStep, Is.EqualTo(1.0 / 60.0));
        Engine.Shutdown();
        // Cycle 1's module saw exactly its own cycle -- cycle 2's shutdown must not have re-run it.
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "tick alpha", "shutdown alpha" }));
        Assert.That(secondJournal, Is.EqualTo(new[] { "init alpha", "tick alpha", "shutdown alpha" }));
    }

    [Test]
    public void ShutdownRunsModuleShutdownInReverseInitOrder()
    {
        List<string> journal = new List<string>();
        // Registered dependent-first: init reorders to alpha, bravo; shutdown must run the reverse of what actually ran -- bravo, alpha.
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { new ModuleBravo(journal), new ModuleAlpha(journal) });
        Engine.PumpEvents();
        Engine.Shutdown();
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "init bravo", "shutdown bravo", "shutdown alpha" }));
    }

    [Test]
    public void ShutdownBeforeReadyRunsNoModuleShutdowns()
    {
        // Modules whose Initialize never ran must not see Shutdown -- the teardown list is the RECORDED init order, not the registration list.
        List<string> journal = new List<string>();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { new ModuleAlpha(journal) });
        Engine.Shutdown();
        Assert.That(journal, Is.Empty);
    }

    [Test]
    public void ShutdownIsIdempotentAndCallsAfterShutdownThrow()
    {
        Engine.Initialize(Config, (level, message) => { }, Array.Empty<IModule>());
        Engine.PumpEvents();
        Engine.Shutdown();
        Engine.Shutdown();
        Assert.Throws<InvalidOperationException>(Engine.PumpEvents);
        Assert.Throws<InvalidOperationException>(() => Engine.Tick(0.016));
        Assert.Throws<InvalidOperationException>(Engine.Render);
        Assert.Throws<InvalidOperationException>(Engine.QueueExit);
        Assert.Throws<InvalidOperationException>(() => Engine.NotifyLifecycle(LifecycleEvent.Resumed));
    }

    [Test]
    public void InitializeTwiceWithoutShutdownThrows()
    {
        Engine.Initialize(Config, (level, message) => { }, Array.Empty<IModule>());
        Assert.Throws<InvalidOperationException>(() => Engine.Initialize(Config, (level, message) => { }, Array.Empty<IModule>()));
        Engine.Shutdown();
    }

    [Test]
    public void PostShutdownReadsStayValidUntilTheNextInitialize()
    {
        Engine.Initialize(Config, (level, message) => { }, Array.Empty<IModule>());
        Engine.PumpEvents();
        Engine.Tick(0.016);
        Engine.QueueExit();
        Engine.Shutdown();
        // A host wrapper reading the flags after teardown is a plausible consumer; the reset happens at the NEXT Initialize, not here.
        Assert.That(Engine.ExitQueued, Is.True);
        Assert.That(Engine.TickCount, Is.EqualTo(1));
        Assert.That(Engine.IsReady, Is.True);
    }

    [Test]
    public void LifecycleEventReachesModuleSubscribers()
    {
        // The consumer proof for the lifecycle vocabulary: no host produces these yet (recorded in CHANGELOG), but the channel itself is exercised -- host-called NotifyLifecycle, module-subscribed delivery.
        ModuleObservesLifecycle module = new ModuleObservesLifecycle();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { module });
        Engine.PumpEvents();
        Engine.NotifyLifecycle(LifecycleEvent.FocusLost);
        Engine.NotifyLifecycle(LifecycleEvent.Suspended);
        Assert.That(module.Observed, Is.EqualTo(new[] { LifecycleEvent.FocusLost, LifecycleEvent.Suspended }));
        Engine.Shutdown();
    }

    private class ModuleThrowsInInit : IModule
    {
        public Type[] Dependencies
        {
            // Depends on alpha so it initializes strictly after it -- the test needs a completed prefix.
            get { return new[] { typeof(ModuleAlpha) }; }
        }

        public void Initialize()
        {
            throw new InvalidOperationException("deliberate init failure");
        }

        public void Shutdown()
        {
            throw new InvalidOperationException("Shutdown must never run for a module whose Initialize failed");
        }

        public void PumpEvents()
        {
        }

        public void Tick(double dt)
        {
        }
    }

    private class ModuleShutdownThrows : IModule
    {
        private readonly List<string> journal;

        public ModuleShutdownThrows(List<string> journal)
        {
            this.journal = journal;
        }

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize()
        {
            journal.Add("init thrower");
        }

        public void Shutdown()
        {
            journal.Add("shutdown thrower");
            throw new InvalidOperationException("deliberate shutdown failure");
        }

        public void PumpEvents()
        {
        }

        public void Tick(double dt)
        {
        }
    }

    [Test]
    public void FailedInitTearsDownTheCompletedPrefixOnly()
    {
        // The recorded-prefix semantic, pinned: alpha's Initialize completed before the failure, so Shutdown runs exactly "shutdown alpha" -- the failed module's own Shutdown must never run (its Shutdown throws if it does).
        List<string> journal = new List<string>();
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { new ModuleAlpha(journal), new ModuleThrowsInInit() });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("deliberate init failure"));
        Engine.Shutdown();
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "shutdown alpha" }));
    }

    [Test]
    public void ThrowingModuleShutdownIsReportedAndTeardownContinues()
    {
        // The policy pinned: a throwing module Shutdown is reported through the log pipeline and the reverse walk continues -- earlier-inited modules still tear down, and Shutdown itself completes.
        List<string> journal = new List<string>();
        List<string> reported = new List<string>();
        Engine.Initialize(Config, (level, message) => reported.Add(message), new IModule[] { new ModuleAlpha(journal), new ModuleShutdownThrows(journal) });
        Engine.PumpEvents();
        Engine.Shutdown();
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "init thrower", "shutdown thrower", "shutdown alpha" }));
        Assert.That(reported.FindAll(message => message.Contains("threw during Shutdown")), Has.Count.EqualTo(1));
        Assert.That(reported.FindAll(message => message.Contains("deliberate shutdown failure")), Has.Count.EqualTo(1));
    }

    private class ModuleCycleA : IModule
    {
        public Type[] Dependencies
        {
            get { return new[] { typeof(ModuleCycleB) }; }
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
        }
    }

    private class ModuleCycleB : IModule
    {
        public Type[] Dependencies
        {
            get { return new[] { typeof(ModuleCycleA) }; }
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
        }
    }

    [Test]
    public void FailedInitWedgesUntilShutdownAndReinitializeRecovers()
    {
        Engine.Initialize(Config, (level, message) => { }, new IModule[] { new ModuleCycleA(), new ModuleCycleB() });
        Assert.Throws<InvalidOperationException>(Engine.PumpEvents);
        // Re-pumping must not silently re-run whatever initialized before the failure; the wedge says so explicitly.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("previously failed"));
        Assert.That(Engine.IsReady, Is.False);
        // The recovery path the static lifecycle adds: Shutdown + a fresh Initialize works.
        Engine.Shutdown();
        Engine.Initialize(Config, (level, message) => { }, Array.Empty<IModule>());
        Engine.PumpEvents();
        Assert.That(Engine.IsReady, Is.True);
        Engine.Shutdown();
    }
}
