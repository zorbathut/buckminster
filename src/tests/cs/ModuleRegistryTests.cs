using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// The module registry's two ordering contracts (init = stable topological order with boot-list-order tiebreak; tick = pure boot-list order) and its loud failure modes (cycles named, missing dependencies named, duplicates rejected at Initialize). Registration is the host-assembled boot list handed to Engine.Initialize -- there is no post-Initialize registration surface at all.
[TestFixture]
public class ModuleRegistryTests
{
    // Test modules record their lifecycle into a shared journal so ordering is asserted on evidence, not inference.
    private class ModuleRecorder : IModule
    {
        private readonly List<string> journal;
        private readonly string name;
        private readonly Type[] dependencies;

        public ModuleRecorder(List<string> journal, string name, params Type[] dependencies)
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
        }

        public void PumpEvents()
        {
        }

        public void Tick(double dt)
        {
            journal.Add($"tick {name}");
        }
    }

    // Distinct concrete types for dependency declarations (the registry keys by concrete type, so ModuleRecorder instances can't depend on each other).
    private class ModuleAlpha : ModuleRecorder
    {
        public ModuleAlpha(List<string> journal) : base(journal, "alpha")
        {
        }
    }

    private class ModuleBravo : ModuleRecorder
    {
        public ModuleBravo(List<string> journal) : base(journal, "bravo", typeof(ModuleAlpha))
        {
        }
    }

    private class ModuleCharlie : ModuleRecorder
    {
        public ModuleCharlie(List<string> journal) : base(journal, "charlie", typeof(ModuleBravo))
        {
        }
    }

    private class ModuleEmpty : IModule
    {
        private readonly Type[] dependencies;

        public ModuleEmpty(params Type[] dependencies)
        {
            this.dependencies = dependencies;
        }

        public Type[] Dependencies
        {
            get { return dependencies; }
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

    private class ModuleCycleA : ModuleEmpty
    {
        public ModuleCycleA() : base(typeof(ModuleCycleB))
        {
        }
    }

    private class ModuleCycleB : ModuleEmpty
    {
        public ModuleCycleB() : base(typeof(ModuleCycleA))
        {
        }
    }

    private class ModuleCycleSelf : ModuleEmpty
    {
        public ModuleCycleSelf() : base(typeof(ModuleCycleSelf))
        {
        }
    }

    // A chain that leads INTO the cycle without being part of it -- exercises DescribeCycle's lead-in trim.
    private class ModuleChain : ModuleEmpty
    {
        public ModuleChain() : base(typeof(ModuleCycleA))
        {
        }
    }

    [Test]
    public void InitIsTopoOrderTickIsBootListOrder()
    {
        List<string> journal = new List<string>();
        // Listed dependent-first: init must reorder to alpha, bravo; tick must NOT reorder.
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleBravo(journal), new ModuleAlpha(journal) });
        Engine.PumpEvents();
        Engine.Tick(0.016);
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "init bravo", "tick bravo", "tick alpha" }));
    }

    [Test]
    public void IndependentModulesInitInBootListOrder()
    {
        List<string> journal = new List<string>();
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleBravo(journal), new ModuleAlpha(journal), new ModuleCharlie(journal) });
        Engine.PumpEvents();
        // Topo constraints: alpha before bravo, bravo before charlie. The stable tiebreak keeps everything else in boot-list order.
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "init bravo", "init charlie" }));
    }

    [Test]
    public void CycleThrowsNamingThePath()
    {
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleCycleA(), new ModuleCycleB() });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleCycleA -> ModuleCycleB -> ModuleCycleA"));
    }

    [Test]
    public void MissingDependencyThrowsNamingModuleAndDependency()
    {
        List<string> journal = new List<string>();
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleBravo(journal) });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleBravo"));
        Assert.That(error.Message, Does.Contain("ModuleAlpha"));
    }

    [Test]
    public void DuplicateConcreteTypeThrowsAtInitialize()
    {
        List<string> journal = new List<string>();
        // Validated before anything irreversible happens, so the failed Initialize leaves the process clean for the next cycle (proven by the scope below succeeding).
        Assert.Throws<InvalidOperationException>(() => new EngineScope(modules: new IModule[] { new ModuleAlpha(journal), new ModuleAlpha(journal) }));
        using EngineScope scope = new EngineScope();
    }

    [Test]
    public void SelfDependencyIsACycle()
    {
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleCycleSelf() });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleCycleSelf -> ModuleCycleSelf"));
    }

    [Test]
    public void CycleMessageTrimsTheLeadInChain()
    {
        // Listed first, so the cycle walk starts at the chain module and must trim it out of the reported loop.
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleChain(), new ModuleCycleA(), new ModuleCycleB() });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleCycleA -> ModuleCycleB -> ModuleCycleA"));
        Assert.That(error.Message, Does.Not.Contain("ModuleChain"));
    }

    [Test]
    public void TickBeforeReadyIsANoOp()
    {
        List<string> journal = new List<string>();
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleAlpha(journal) });
        // PLAN.md's host idiom is pump-and-tick until Ready, so a pre-Ready Tick must be harmless: no module ticks, no count advance.
        Engine.Tick(0.016);
        Assert.That(Engine.IsReady, Is.False);
        Assert.That(Engine.TickCount, Is.Zero);
        Assert.That(journal, Is.Empty);
        Engine.PumpEvents();
        Assert.That(Engine.IsReady, Is.True);
        Engine.Tick(0.016);
        Assert.That(Engine.TickCount, Is.EqualTo(1));
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "tick alpha" }));
    }

    [Test]
    public void TickCountCountsCompletedTicks()
    {
        using EngineScope scope = new EngineScope();
        Engine.PumpEvents();
        Engine.Tick(0.016);
        Engine.Tick(0.016);
        Engine.Tick(0.016);
        Assert.That(Engine.TickCount, Is.EqualTo(3));
    }
}
