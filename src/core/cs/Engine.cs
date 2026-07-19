using System;
using System.Text;
using System.Collections.Generic;
using Buckminster.Ffi;

namespace Buckminster;

// The static true-globals facade (M5.75: process-global concerns live here because they ARE process-global -- one log pipeline, one module list, one exit flag; the multi-instantiable render-residency unit is Demesne, arriving with the ABI split). Lifecycle: Initialize(config, sink, modules) -> pump until IsReady -> the host drives MainLoop -> Shutdown. Module init runs inside PumpEvents because init is async-shaped from the foundation up -- M4 completes it in one pump, but hosts must not assume that. Initialize/Shutdown are cyclable within a process: INITIALIZE is what returns every static (including the Lifecycle invocation list) to virgin, so post-Shutdown reads stay valid until the next cycle begins. All state is host-thread single-threaded; per-instance concurrency returns with Demesne.
public static class Engine
{
    private static bool initialized;
    // The init lifecycle latch: a failed module init wedges the engine loudly -- re-pumping after a failure would silently re-run the Initializes that succeeded before it. Recovery is Shutdown + a fresh Initialize.
    private static bool initFailed;
    private static readonly List<IModule> modules = new List<IModule>();
    // The recorded actual init order (topo order isn't unique); Shutdown walks it in reverse, and only over modules whose Initialize completed.
    private static readonly List<IModule> initOrder = new List<IModule>();

    public static bool IsReady { get; private set; }

    // The deferred exit flag (GLFW WindowShouldClose lineage): QueueExit's caller is typically a module mid-Tick, sitting under the engine's own iteration, where synchronous teardown is impossible -- so the frame finishes and the host loop condition is what honors the flag. The engine itself never acts on it (ticking past it is legal and fully functional; MainLoop's mid-burst check is pacing policy, not engine policy). Read stays valid post-Shutdown like IsReady/TickCount.
    public static bool ExitQueued { get; private set; }

    // Completed ticks, assigned from the native counter's return after the modules and the native tick have all run -- a thrown module Tick advances neither side, and the two cannot desync.
    public static ulong TickCount { get; private set; }

    // App-lifecycle notifications (LifecycleEvent doc): hosts call NotifyLifecycle, modules and the game subscribe here (typically in their Initialize). The invocation list is cleared by the next Initialize, so cycle N-1's subscribers can never ghost into cycle N.
    public static event Action<LifecycleEvent>? Lifecycle;

    // The log sink is mandatory: silent log dropping is banned and there is no principled "absent" value -- a host that truly wants to discard logs writes that decision down as a discarding delegate. The sink registration is process-global last-wins on the Rust side; raw-FFI tests that register their own sinks around an initialized Engine inherit that contract knowingly.
    public static void Initialize(EngineConfig config, Action<LogLevel, string> logSink, IReadOnlyList<IModule> bootModules)
    {
        if (initialized)
        {
            throw new InvalidOperationException("Engine is already initialized; Shutdown first (Initialize/Shutdown cycles are supported)");
        }
        // Duplicate concrete types make every dependency declaration on that type ambiguous; the boot list is validated before anything irreversible happens.
        HashSet<Type> moduleTypes = new HashSet<Type>();
        foreach (IModule module in bootModules)
        {
            if (!moduleTypes.Add(module.GetType()))
            {
                throw new InvalidOperationException($"module type {module.GetType().Name} appears twice in the boot list; dependencies are declared by concrete type, so duplicates would be ambiguous");
            }
        }
        Native.GlobalsInit(config);
        LogSinkVtable vtable = LogSinkThunks.Create(new SinkAdapter(logSink));
        try
        {
            // buck_log_sink_set's own exit is the first delivery point, and the buffer may hold residue (a previous cycle's pushback tail); if the new sink throws on it, the just-initialized Rust globals and the registration must not leak.
            Native.LogSinkSet(in vtable);
        }
        catch
        {
            // Cleanup ORDER is load-bearing: clear the Rust registration FIRST (dropping the Rust-side proxy fires the release thunk, which unregisters the callback key deterministically), so nothing later can fire a dead key. The two return codes are deliberately unchecked raw calls: with the registration already cleared they can only fail via states that would themselves have thrown above, and the sink's original exception must win. Known narrow gap: if the set failed BEFORE storing (today only the poisoned-mutex panic path), the proxy was never registered Rust-side, clear releases nothing, and the key leaks -- accepted for a path that already means the process is broken.
            NativeMethods.buck_log_sink_clear();
            NativeMethods.buck_globals_shutdown();
            throw;
        }
        // Point of no return: reset every static to virgin AFTER the fallible work, so a failed Initialize leaves the previous cycle's post-Shutdown reads intact.
        initialized = true;
        initFailed = false;
        IsReady = false;
        ExitQueued = false;
        TickCount = 0;
        Lifecycle = null;
        modules.Clear();
        modules.AddRange(bootModules);
        initOrder.Clear();
        MainLoop.ResetPacing();
    }

    public static void Shutdown()
    {
        if (!initialized)
        {
            // Idempotent like the old Dispose: teardown paths (finally blocks, scope helpers) may run it twice.
            return;
        }
        initialized = false;
        try
        {
            // Module teardown first, in reverse recorded init order, while the engine's services are still up -- a module's Shutdown may legitimately log. Policy: a throwing module Shutdown must not hold the REST of teardown hostage, so its exception is reported through the log pipeline (loud, synchronous via the sink) and the walk continues; Shutdown itself only throws from the native-teardown path below. (If the sink itself throws while delivering that report, that exception surfaces from here -- the throwing-sink family's normal behavior.)
            for (int i = initOrder.Count - 1; i >= 0; i--)
            {
                try
                {
                    initOrder[i].Shutdown();
                }
                catch (Exception exception)
                {
                    Log.Error($"module {initOrder[i].GetType().Name} threw during Shutdown (continuing teardown): {exception}");
                }
            }
        }
        finally
        {
            try
            {
                // The globals shutdown's own exit-drain delivers the buffered tail -- including records logged by module Shutdowns -- through the still-registered log sink. A throwing sink surfaces from here, but teardown is not hostage to it: the finally clears the registration either way.
                Native.GlobalsShutdown();
            }
            finally
            {
                // Clearing drops the Rust-side proxy, whose release thunk unregisters the callback key -- no manual bookkeeping.
                Native.LogSinkClear();
            }
        }
    }

    // Idempotent, and legal pre-Ready (a module may queue exit from its own Initialize).
    public static void QueueExit()
    {
        ThrowIfNotInitialized();
        ExitQueued = true;
    }

    // The host-called lifecycle entry (LifecycleEvent doc). No host produces these yet -- the channel ships consumer-proven by test, producer-pending.
    public static void NotifyLifecycle(LifecycleEvent lifecycleEvent)
    {
        ThrowIfNotInitialized();
        Lifecycle?.Invoke(lifecycleEvent);
    }

    public static void PumpEvents()
    {
        ThrowIfNotInitialized();
        if (initFailed)
        {
            throw new InvalidOperationException("module initialization previously failed; the engine is wedged -- Shutdown and Initialize a fresh cycle");
        }
        if (!IsReady)
        {
            try
            {
                InitializeModules();
            }
            catch
            {
                initFailed = true;
                throw;
            }
            IsReady = true;
        }
        else
        {
            // Module pumps run on Ready-and-later pumps only -- the init pump runs none, so a module's PumpEvents is never called before its Initialize (the IModule contract, pinned by MainLoopTests/EnginePumpTests).
            foreach (IModule module in modules)
            {
                module.PumpEvents();
            }
        }
    }

    public static void Tick(double dt)
    {
        ThrowIfNotInitialized();
        if (!IsReady)
        {
            // The host idiom is pump-and-tick until Ready (PLAN.md async-shaped init), so a pre-Ready Tick is a harmless no-op, not an error: no module runs, no counter advances, sim tick 0 stays pinned to Ready.
            return;
        }
        foreach (IModule module in modules)
        {
            module.Tick(dt);
        }
        // Assigning the native counter's return keeps the two sides trivially in sync -- and a thrown module Tick above means neither advanced.
        TickCount = Native.GlobalsTick(dt);
    }

    public static void Render()
    {
        ThrowIfNotInitialized();
        // No render layer until M6; the seam is the point (PLAN.md: Tick and Render are separate calls even though desktop always pairs them). Its per-demesne/per-window scoping is an M6 design input.
    }

    // Init in stable topological order: repeatedly take the first listed module whose dependencies are all initialized, so ordering is fully determined by declarations + boot-list order (deterministic, per the Determinism rules). Every completed Initialize is recorded into initOrder for Shutdown's reverse walk -- including ones that ran before a later module's init failure.
    private static void InitializeModules()
    {
        HashSet<Type> moduleTypes = new HashSet<Type>();
        foreach (IModule module in modules)
        {
            moduleTypes.Add(module.GetType());
        }
        foreach (IModule module in modules)
        {
            foreach (Type dependency in module.Dependencies)
            {
                if (!moduleTypes.Contains(dependency))
                {
                    throw new InvalidOperationException($"module {module.GetType().Name} depends on {dependency.Name}, which is not in the boot list");
                }
            }
        }
        List<IModule> remaining = new List<IModule>(modules);
        HashSet<Type> initialized = new HashSet<Type>();
        while (remaining.Count > 0)
        {
            int nextIndex = -1;
            for (int i = 0; i < remaining.Count; i++)
            {
                bool satisfied = true;
                foreach (Type dependency in remaining[i].Dependencies)
                {
                    if (!initialized.Contains(dependency))
                    {
                        satisfied = false;
                        break;
                    }
                }
                if (satisfied)
                {
                    nextIndex = i;
                    break;
                }
            }
            if (nextIndex < 0)
            {
                throw new InvalidOperationException($"module dependency cycle: {DescribeCycle(remaining)}");
            }
            IModule next = remaining[nextIndex];
            remaining.RemoveAt(nextIndex);
            next.Initialize();
            initOrder.Add(next);
            initialized.Add(next.GetType());
        }
    }

    // Walk dependency edges among the stuck modules until a type repeats; the walk can't escape (every stuck module has at least one stuck dependency, else it would have initialized).
    private static string DescribeCycle(List<IModule> remaining)
    {
        Dictionary<Type, IModule> byType = new Dictionary<Type, IModule>();
        foreach (IModule module in remaining)
        {
            byType[module.GetType()] = module;
        }
        List<Type> path = new List<Type>();
        HashSet<Type> visited = new HashSet<Type>();
        Type current = remaining[0].GetType();
        while (visited.Add(current))
        {
            path.Add(current);
            foreach (Type dependency in byType[current].Dependencies)
            {
                if (byType.ContainsKey(dependency))
                {
                    current = dependency;
                    break;
                }
            }
        }
        // Trim the lead-in so the message shows exactly the loop, closed by repeating its first node.
        int loopStart = path.IndexOf(current);
        path.RemoveRange(0, loopStart);
        path.Add(current);
        StringBuilder cycle = new StringBuilder();
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                cycle.Append(" -> ");
            }
            cycle.Append(path[i].Name);
        }
        return cycle.ToString();
    }

    // Adapts the host's sink delegate to the generated ILogSink interface; LogSinkThunks carries it across the boundary.
    private sealed class SinkAdapter : ILogSink
    {
        private readonly Action<LogLevel, string> sink;

        public SinkAdapter(Action<LogLevel, string> sink)
        {
            this.sink = sink;
        }

        public void Write(LogLevel level, string msg)
        {
            sink(level, msg);
        }
    }

    private static void ThrowIfNotInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("Engine is not initialized; call Engine.Initialize first");
        }
    }
}
