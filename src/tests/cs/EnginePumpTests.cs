using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// IModule.PumpEvents: the per-pump hook that carries the platform module's winit pump (PLAN M5). The lifecycle contract pinned here: a module's PumpEvents is only ever called after its Initialize has completed -- in the current one-pump init world that means the init pump runs NO module pumps, and every later pump runs them in boot-list order.
[TestFixture]
public class EnginePumpTests
{
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

        public void Initialize()
        {
            journal.Add($"init:{name}");
        }

        public void Shutdown()
        {
        }

        public void PumpEvents()
        {
            journal.Add($"pump:{name}");
        }

        public void Tick(double dt)
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

        public void Initialize()
        {
            inner.Initialize();
        }

        public void Shutdown()
        {
        }

        public void PumpEvents()
        {
            inner.PumpEvents();
        }

        public void Tick(double dt)
        {
            inner.Tick(dt);
        }
    }

    [Test]
    public void InitPumpRunsNoModulePumpsAndLaterPumpsRunInBootListOrder()
    {
        List<string> journal = new List<string>();
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleJournal(journal, "a"), new ModuleJournalSecond(journal, "b") });
        Engine.PumpEvents();
        // The init pump: Initializes ran, module PumpEvents did NOT (a module's PumpEvents is only ever called after ALL init completed -- pinned floor for the future multi-pump async init question).
        Assert.That(journal, Is.EqualTo(new[] { "init:a", "init:b" }));
        Engine.PumpEvents();
        Assert.That(journal, Is.EqualTo(new[] { "init:a", "init:b", "pump:a", "pump:b" }));
        Engine.Tick(0.016);
        Engine.PumpEvents();
        Assert.That(journal, Is.EqualTo(new[] { "init:a", "init:b", "pump:a", "pump:b", "tick:a", "tick:b", "pump:a", "pump:b" }));
    }
}
