using System;
using Buckminster.Ffi;

namespace Buckminster;

// The desktop platform module: the thin managed face of the Rust winit layer (src/core/rust/platform.rs). ModuleWindow holds this and orchestrates the pump/poll; this module's own hooks are deliberately empty beyond fail-fast init. Inherits the Rust layer's contracts: process singleton, thread-bound to the first-calling thread, unavailable (loud error) on wasm and headless environments.
public sealed class ModulePlatformDesktop : IModule
{
    public Type[] Dependencies
    {
        get { return Type.EmptyTypes; }
    }

    public void Initialize(Engine engine)
    {
        // Fail fast: the first pump builds the event loop (and claims the thread), so a headless environment errors here at init -- loudly, at pump-to-Ready -- instead of at the first window create.
        Native.PlatformPump();
    }

    public void PumpEvents(Engine engine)
    {
    }

    public void Tick(Engine engine, double dt)
    {
    }

    internal void Pump()
    {
        Native.PlatformPump();
    }

    internal (uint Written, uint Remaining) Poll(Span<PlatformEventRaw> buffer)
    {
        return Native.PlatformEventsPoll(buffer);
    }

    internal ulong CreateNativeWindow(string title, uint width, uint height)
    {
        return Native.WindowCreate(title, width, height);
    }

    internal void DestroyNativeWindow(ulong window)
    {
        Native.WindowDestroy(window);
    }
}
