using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Buckminster.Ffi;

namespace Buckminster;

// The friendly C# face of the engine (PLAN.md tick-as-callee: hosts own the loop and call PumpEvents/Tick/Render; the engine never runs one). Wraps the Rust engine handle and owns the module registry. Create, register modules, then pump until IsReady; module init runs inside PumpEvents because init is async-shaped from the foundation up -- M4 completes it in one pump, but hosts must not assume that.
public sealed class Engine : IDisposable
{
    private ulong handle;
    private bool disposed;
    // The init lifecycle latches: started closes registration (including from inside a module's own Initialize), failed wedges the engine loudly -- re-pumping after a failed init would silently re-run the Initializes that succeeded before the failure.
    private bool initStarted;
    private bool initFailed;
    private readonly ulong logSinkKey;
    private readonly List<IModule> modules = new List<IModule>();
    private readonly HashSet<Type> moduleTypes = new HashSet<Type>();

    public bool IsReady { get; private set; }

    // Completed ticks. Increments only after the modules and the native tick have all run, so a thrown module Tick can't desync this from the Rust-side counter.
    public ulong TickCount { get; private set; }

    private Engine(ulong handle, ulong logSinkKey)
    {
        this.handle = handle;
        this.logSinkKey = logSinkKey;
    }

    // The log sink is mandatory: silent log dropping is banned and there is no principled "absent" value -- a host that truly wants to discard logs writes that decision down as a discarding delegate. Registration is process-global last-wins (logging is process-scoped; multi-engine separation is a non-goal -- which also covers the known sharp edge that a DIFFERENT live engine's sink throwing during this create's exit-drain surfaces here as CallbackError and strands the new Rust-side handle, since the out-param is unspecified on nonzero returns and cannot be destroyed).
    public static unsafe Engine Create(EngineConfig config, Action<LogLevel, string> logSink)
    {
        FfiCall.ThrowOnError(NativeMethods.buck_engine_create(config, out ulong handle), "engine create");
        ulong logSinkKey = CallbackTable.Register(logSink);
        try
        {
            // buck_log_sink_set's own exit is the first delivery point, and the buffer may hold residue (a previous engine's pushback tail); if the new sink throws on it, the just-created handle and key must not leak.
            FfiCall.ThrowOnError(NativeMethods.buck_log_sink_set((IntPtr)(delegate* unmanaged<ulong, int, byte*, nuint, int>)&LogSinkThunk, logSinkKey), "log sink registration");
        }
        catch
        {
            // Cleanup ORDER is load-bearing: clear the Rust registration FIRST (its own exit-drain copies None and delivers nothing), so the unregister and destroy below can never fire the now-dead key -- reversing this leaves a dangling registration whose dead key breaks every subsequent create in the process. The two return codes are deliberately unchecked: with the registration already cleared they can only fail via states that would themselves have thrown above, and the sink's original exception must win.
            NativeMethods.buck_log_sink_clear();
            CallbackTable.Unregister(logSinkKey);
            NativeMethods.buck_engine_destroy(handle);
            throw;
        }
        return new Engine(handle, logSinkKey);
    }

    // Registration is open until init starts (the first PumpEvents); dependencies are declared by concrete type, so a second instance of the same type would make every dependency on it ambiguous.
    public void RegisterModule(IModule module)
    {
        ThrowIfDisposed();
        if (initStarted)
        {
            throw new InvalidOperationException($"cannot register {module.GetType().Name}: module registration closes when initialization starts (the first PumpEvents)");
        }
        if (!moduleTypes.Add(module.GetType()))
        {
            throw new InvalidOperationException($"module type {module.GetType().Name} is already registered; dependencies are declared by concrete type, so duplicates would be ambiguous");
        }
        modules.Add(module);
    }

    public void PumpEvents()
    {
        ThrowIfDisposed();
        if (initFailed)
        {
            throw new InvalidOperationException("module initialization previously failed; the engine is unusable -- dispose it and create a fresh one");
        }
        if (!IsReady)
        {
            initStarted = true;
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
    }

    public void Tick(double dt)
    {
        ThrowIfDisposed();
        if (!IsReady)
        {
            // The host idiom is pump-and-tick until Ready (PLAN.md async-shaped init), so a pre-Ready Tick is a harmless no-op, not an error: no module runs, no counter advances, sim tick 0 stays pinned to Ready.
            return;
        }
        foreach (IModule module in modules)
        {
            module.Tick(this, dt);
        }
        FfiCall.ThrowOnError(NativeMethods.buck_engine_tick(handle, dt, out _), "engine tick");
        TickCount += 1;
    }

    public void Render()
    {
        // Deliberate no-op until the render layer exists (M6); the seam is the point (PLAN.md: Tick and Render are separate calls even though desktop always pairs them).
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            // Destroy FIRST: its own exit-drain delivers the buffered tail -- including records logged by destroy itself -- through the still-registered log sink. A throwing sink surfaces from here, but teardown is not hostage to it: the finally clears the registration either way.
            FfiCall.ThrowOnError(NativeMethods.buck_engine_destroy(handle), "engine destroy");
        }
        finally
        {
            handle = 0;
            FfiCall.ThrowOnError(NativeMethods.buck_log_sink_clear(), "log sink clear");
            CallbackTable.Unregister(logSinkKey);
        }
    }

    // Init in stable topological order: repeatedly take the first registered module whose dependencies are all initialized, so ordering is fully determined by declarations + registration order (deterministic, per the Determinism rules).
    private void InitializeModules()
    {
        foreach (IModule module in modules)
        {
            foreach (Type dependency in module.Dependencies)
            {
                if (!moduleTypes.Contains(dependency))
                {
                    throw new InvalidOperationException($"module {module.GetType().Name} depends on {dependency.Name}, which is not registered");
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
            next.Initialize(this);
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

    // The standard callback thunk shape (static, [UnmanagedCallersOnly], u64 key, exception stashed and reported as a nonzero return -- never thrown across the FFI).
    [UnmanagedCallersOnly]
    private static unsafe int LogSinkThunk(ulong userdata, int level, byte* message, nuint length)
    {
        try
        {
            Action<LogLevel, string> logSink = (Action<LogLevel, string>)CallbackTable.Get(userdata);
            logSink((LogLevel)level, Encoding.UTF8.GetString(message, checked((int)length)));
            return 0;
        }
        catch (Exception exception)
        {
            CallbackExceptionStash.Stash(exception);
            return 1;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
