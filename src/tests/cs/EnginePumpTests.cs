using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// IModule.PumpEvents: the per-pump hook that carries the platform module's winit pump (PLAN M5). The lifecycle contract pinned here: a module's PumpEvents is only ever called after its Initialize has completed -- in the current one-pump init world that means the init pump runs NO module pumps, and every later pump runs them in registration order.
[TestFixture]
public class EnginePumpTests
{
    private static Engine CreateEngine()
    {
        return Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 }, (level, message) => { });
    }

    private class ModuleJournal : IModule
    {
        private readonly List<string> journal;
        private readonly string name;

        public ModuleJournal(List<string> journal, string name)
        {
            this.journal = journal;
            this.name = name;
        }

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            journal.Add($"init:{name}");
        }

        public void PumpEvents(Engine engine)
        {
            journal.Add($"pump:{name}");
        }

        public void Tick(Engine engine, double dt)
        {
            journal.Add($"tick:{name}");
        }
    }

    // Exists only because the registry rejects duplicate concrete types -- the ordering test needs two DISTINCT module types, so this wraps the same journaling behavior under a second identity.
    private class ModuleJournalSecond : IModule
    {
        private readonly ModuleJournal inner;

        public ModuleJournalSecond(List<string> journal, string name)
        {
            inner = new ModuleJournal(journal, name);
        }

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            inner.Initialize(engine);
        }

        public void PumpEvents(Engine engine)
        {
            inner.PumpEvents(engine);
        }

        public void Tick(Engine engine, double dt)
        {
            inner.Tick(engine, dt);
        }
    }

    [Test]
    public void InitPumpRunsNoModulePumpsAndLaterPumpsRunInRegistrationOrder()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleJournal(journal, "a"));
        engine.RegisterModule(new ModuleJournalSecond(journal, "b"));
        engine.PumpEvents();
        // The init pump: Initializes ran, module PumpEvents did NOT (a module's PumpEvents is only ever called after ALL init completed -- pinned floor for the future multi-pump async init question).
        Assert.That(journal, Is.EqualTo(new[] { "init:a", "init:b" }));
        engine.PumpEvents();
        Assert.That(journal, Is.EqualTo(new[] { "init:a", "init:b", "pump:a", "pump:b" }));
        engine.Tick(0.016);
        engine.PumpEvents();
        Assert.That(journal, Is.EqualTo(new[] { "init:a", "init:b", "pump:a", "pump:b", "tick:a", "tick:b", "pump:a", "pump:b" }));
    }

    [Test]
    public void ModulePumpSeesCurrentScoped()
    {
        List<Engine?> observed = new List<Engine?>();
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModulePumpObservesCurrent(observed));
        engine.PumpEvents();
        engine.PumpEvents();
        Assert.That(observed, Is.EqualTo(new[] { engine }));
        Assert.That(Engine.Current, Is.Null);
    }

    private class ModulePumpObservesCurrent : IModule
    {
        private readonly List<Engine?> observed;

        public ModulePumpObservesCurrent(List<Engine?> observed)
        {
            this.observed = observed;
        }

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void PumpEvents(Engine engine)
        {
            observed.Add(Engine.Current);
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }
}
