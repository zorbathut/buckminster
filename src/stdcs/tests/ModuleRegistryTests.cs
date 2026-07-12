using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// The module registry's two ordering contracts (init = stable topological order with registration-order tiebreak; tick = pure registration order) and its loud failure modes (cycles named, missing dependencies named, duplicates rejected, late registration rejected).
[TestFixture]
public class ModuleRegistryTests
{
    private static Engine CreateEngine()
    {
        return Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024 });
    }

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

        public void Initialize(Engine engine)
        {
            journal.Add($"init {name}");
        }

        public void Tick(Engine engine, double dt)
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

    private class ModuleCycleA : IModule
    {
        public Type[] Dependencies
        {
            get { return new[] { typeof(ModuleCycleB) }; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    private class ModuleCycleB : IModule
    {
        public Type[] Dependencies
        {
            get { return new[] { typeof(ModuleCycleA) }; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    [Test]
    public void InitIsTopoOrderTickIsRegistrationOrder()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        // Registered dependent-first: init must reorder to alpha, bravo; tick must NOT reorder.
        engine.RegisterModule(new ModuleBravo(journal));
        engine.RegisterModule(new ModuleAlpha(journal));
        engine.PumpEvents();
        engine.Tick(0.016);
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "init bravo", "tick bravo", "tick alpha" }));
    }

    [Test]
    public void IndependentModulesInitInRegistrationOrder()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleBravo(journal));
        engine.RegisterModule(new ModuleAlpha(journal));
        engine.RegisterModule(new ModuleCharlie(journal));
        engine.PumpEvents();
        // Topo constraints: alpha before bravo, bravo before charlie. The stable tiebreak keeps everything else in registration order.
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "init bravo", "init charlie" }));
    }

    [Test]
    public void CycleThrowsNamingThePath()
    {
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleCycleA());
        engine.RegisterModule(new ModuleCycleB());
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleCycleA -> ModuleCycleB -> ModuleCycleA"));
    }

    [Test]
    public void MissingDependencyThrowsNamingModuleAndDependency()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleBravo(journal));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleBravo"));
        Assert.That(error.Message, Does.Contain("ModuleAlpha"));
    }

    [Test]
    public void DuplicateConcreteTypeThrowsAtRegistration()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleAlpha(journal));
        Assert.Throws<InvalidOperationException>(() => engine.RegisterModule(new ModuleAlpha(journal)));
    }

    [Test]
    public void RegisterAfterInitThrows()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        engine.PumpEvents();
        Assert.Throws<InvalidOperationException>(() => engine.RegisterModule(new ModuleAlpha(journal)));
    }

    private class ModuleCycleSelf : IModule
    {
        public Type[] Dependencies
        {
            get { return new[] { typeof(ModuleCycleSelf) }; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    // A chain that leads INTO the cycle without being part of it -- exercises DescribeCycle's lead-in trim.
    private class ModuleChain : IModule
    {
        public Type[] Dependencies
        {
            get { return new[] { typeof(ModuleCycleA) }; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    private class ModuleRegistersMidInit : IModule
    {
        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            engine.RegisterModule(new ModuleCycleSelf());
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    [Test]
    public void SelfDependencyIsACycle()
    {
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleCycleSelf());
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleCycleSelf -> ModuleCycleSelf"));
    }

    [Test]
    public void CycleMessageTrimsTheLeadInChain()
    {
        using Engine engine = CreateEngine();
        // Registered first, so the cycle walk starts at the chain module and must trim it out of the reported loop.
        engine.RegisterModule(new ModuleChain());
        engine.RegisterModule(new ModuleCycleA());
        engine.RegisterModule(new ModuleCycleB());
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("ModuleCycleA -> ModuleCycleB -> ModuleCycleA"));
        Assert.That(error.Message, Does.Not.Contain("ModuleChain"));
    }

    [Test]
    public void RegistrationFromInsideInitializeThrows()
    {
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleRegistersMidInit());
        // A module registered mid-init would never itself be initialized yet would tick forever -- the registration gate must already be closed.
        Assert.Throws<InvalidOperationException>(engine.PumpEvents);
    }

    [Test]
    public void FailedInitWedgesTheEngineLoudly()
    {
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleCycleA());
        engine.RegisterModule(new ModuleCycleB());
        Assert.Throws<InvalidOperationException>(engine.PumpEvents);
        // Re-pumping must not silently re-run whatever initialized before the failure; the engine is dead, and it says so.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("previously failed"));
        Assert.That(engine.IsReady, Is.False);
    }

    [Test]
    public void TickBeforeReadyIsANoOp()
    {
        List<string> journal = new List<string>();
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleAlpha(journal));
        // PLAN.md's host idiom is pump-and-tick until Ready, so a pre-Ready Tick must be harmless: no module ticks, no count advance.
        engine.Tick(0.016);
        Assert.That(engine.IsReady, Is.False);
        Assert.That(engine.TickCount, Is.Zero);
        Assert.That(journal, Is.Empty);
        engine.PumpEvents();
        Assert.That(engine.IsReady, Is.True);
        engine.Tick(0.016);
        Assert.That(engine.TickCount, Is.EqualTo(1));
        Assert.That(journal, Is.EqualTo(new[] { "init alpha", "tick alpha" }));
    }

    [Test]
    public void TickCountCountsCompletedTicks()
    {
        using Engine engine = CreateEngine();
        engine.PumpEvents();
        engine.Tick(0.016);
        engine.Tick(0.016);
        engine.Tick(0.016);
        Assert.That(engine.TickCount, Is.EqualTo(3));
    }

    [Test]
    public void DisposeIsIdempotentAndTickAfterDisposeThrows()
    {
        Engine engine = CreateEngine();
        engine.PumpEvents();
        engine.Dispose();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.Tick(0.016));
    }
}
