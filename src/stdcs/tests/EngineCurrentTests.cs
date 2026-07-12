using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// Engine.Current: the thread-local ambient engine (Ghi Environment.Current lineage). Instance methods scope it (set-and-restore around PumpEvents/Tick/Render); static lifecycle (Create/Dispose) deliberately doesn't. Journal-style assertions throughout -- asserting inside a module's Initialize would wedge the engine via the initFailed latch and entangle unrelated failures. No cross-thread test: Thread.Start throws on the wasm cells (the test glob compiles everything everywhere) and [ThreadStatic] semantics are BCL-guaranteed.
[TestFixture]
public class EngineCurrentTests
{
    [TearDown]
    public void TearDown()
    {
        // One leaked host-set value would poison every later null assertion on the shared NUnit thread.
        Engine.Current = null;
    }

    private static Engine CreateEngine(Action<LogLevel, string>? logSink = null)
    {
        return Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 }, logSink ?? ((level, message) => { }));
    }

    private class ModuleCurrentRecorder : IModule
    {
        public readonly List<Engine?> Observed = new List<Engine?>();

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            Observed.Add(Engine.Current);
        }

        public void Tick(Engine engine, double dt)
        {
            Observed.Add(Engine.Current);
        }
    }

    private class ModuleDrivesInnerEngine : IModule
    {
        private readonly Engine inner;
        public readonly List<Engine?> Observed = new List<Engine?>();

        public ModuleDrivesInnerEngine(Engine inner)
        {
            this.inner = inner;
        }

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            Observed.Add(Engine.Current);
            inner.PumpEvents();
            inner.Tick(0.016);
            // Back in the outer scope: the inner engine's restore must have brought the outer engine back.
            Observed.Add(Engine.Current);
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    private class ModuleObservesInner : IModule
    {
        public readonly List<Engine?> Observed = new List<Engine?>();

        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
            Observed.Add(Engine.Current);
        }
    }

    private class ModuleThrowsInInit : IModule
    {
        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
            throw new InvalidOperationException("deliberate init failure");
        }

        public void Tick(Engine engine, double dt)
        {
        }
    }

    [Test]
    public void CurrentIsScopedToEngineCalls()
    {
        Assert.That(Engine.Current, Is.Null);
        ModuleCurrentRecorder module = new ModuleCurrentRecorder();
        using Engine engine = CreateEngine();
        engine.RegisterModule(module);
        engine.PumpEvents();
        Assert.That(Engine.Current, Is.Null);
        engine.Tick(0.016);
        Assert.That(Engine.Current, Is.Null);
        Assert.That(module.Observed, Is.EqualTo(new[] { engine, engine }));
    }

    [Test]
    public void NestedEngineCallsSaveAndRestore()
    {
        using Engine inner = CreateEngine();
        ModuleObservesInner innerModule = new ModuleObservesInner();
        inner.RegisterModule(innerModule);
        using Engine outer = CreateEngine();
        ModuleDrivesInnerEngine outerModule = new ModuleDrivesInnerEngine(inner);
        outer.RegisterModule(outerModule);
        outer.PumpEvents();
        // The outer module saw: outer (before driving inner), then outer again (after inner's calls restored it); the inner module saw inner during its tick.
        Assert.That(outerModule.Observed, Is.EqualTo(new[] { outer, outer }));
        Assert.That(innerModule.Observed, Is.EqualTo(new[] { inner }));
        Assert.That(Engine.Current, Is.Null);
    }

    [Test]
    public void CurrentRestoresWhenInitThrows()
    {
        using Engine engine = CreateEngine();
        engine.RegisterModule(new ModuleThrowsInInit());
        Assert.Throws<InvalidOperationException>(engine.PumpEvents);
        // The finally must restore even on the exception path -- the single easiest future regression.
        Assert.That(Engine.Current, Is.Null);
    }

    [Test]
    public void LogSinkDeliveryObservesCurrent()
    {
        // The load-bearing property for future per-engine log routing: the sink fires inside the emitting call's scope, so Current at delivery time identifies the engine. Filtered by message rather than asserted as the whole sequence: the process-global buffer may legally deliver another fixture's stranded residue at registration time.
        List<(Engine? Observed, string Message)> observedAtDelivery = new List<(Engine?, string)>();
        using Engine engine = CreateEngine((level, message) => observedAtDelivery.Add((Engine.Current, message)));
        engine.RegisterModule(new ModuleLogsInTick());
        engine.PumpEvents();
        engine.Tick(0.016);
        Assert.That(observedAtDelivery.FindAll(entry => entry.Message == "from inside tick"), Is.EqualTo(new[] { ((Engine?)engine, "from inside tick") }));
    }

    [Test]
    public void RenderOnDisposedEngineThrows()
    {
        Engine engine = CreateEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(engine.Render);
    }

    private class ModuleLogsInTick : IModule
    {
        public Type[] Dependencies
        {
            get { return Type.EmptyTypes; }
        }

        public void Initialize(Engine engine)
        {
        }

        public void Tick(Engine engine, double dt)
        {
            Log.Info("from inside tick");
        }
    }

    [Test]
    public void HostSetCurrentSurvivesOtherEnginesCalls()
    {
        using Engine mine = CreateEngine();
        using Engine other = CreateEngine();
        Engine.Current = mine;
        other.PumpEvents();
        other.Tick(0.016);
        // Save/restore, not set/clear: the other engine's scoped calls must hand back whatever was current before.
        Assert.That(Engine.Current, Is.SameAs(mine));
    }
}
