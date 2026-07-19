using System;
using Buckminster.Host.Desktop.Ffi;
// A type alias, not `using Buckminster.Ffi;`: importing core's plumbing namespace wholesale would make the bare Native/NativeMethods below ambiguous between core's classes (IVT-visible) and this assembly's own.
using PlatformEventRaw = Buckminster.Ffi.PlatformEventRaw;

namespace Buckminster.Host.Desktop;

// The desktop platform module: the thin managed face of this assembly's Rust winit layer (src/host-desktop/rust/platform.rs; hoisted out of core at M5.75 -- platform code is host territory). Implements core's IPlatformWindowing seam for ModuleWindow. Inherits the Rust layer's contracts: process singleton, thread-bound to the first-calling thread, loud error in headless environments.
public sealed class ModulePlatformDesktop : IModule, IPlatformWindowing
{
    public Type[] Dependencies
    {
        get { return Type.EmptyTypes; }
    }

    public void Initialize()
    {
        // Fail fast: the first pump builds the event loop (and claims the thread), so a headless environment errors here at init -- loudly, at pump-to-Ready -- instead of at the first window create.
        Native.PlatformPump();
    }

    public void Shutdown()
    {
        // The Rust platform layer has no teardown surface (the process-singleton event loop lives until process exit); windows are destroyed by their owners (ModuleWindow's Shutdown, ordered before this by the dependency edge).
    }

    public void PumpEvents()
    {
    }

    public void Tick(double dt)
    {
    }

    void IPlatformWindowing.Pump()
    {
        Native.PlatformPump();
    }

    (uint Written, uint Remaining) IPlatformWindowing.Poll(Span<PlatformEventRaw> buffer)
    {
        return Native.PlatformEventsPoll(buffer);
    }

    ulong IPlatformWindowing.CreateNativeWindow(string title, uint width, uint height)
    {
        return Native.WindowCreate(title, width, height);
    }

    void IPlatformWindowing.DestroyNativeWindow(ulong window)
    {
        Native.WindowDestroy(window);
    }

    void IPlatformWindowing.SetNativeWindowTitle(ulong window, string title)
    {
        Native.WindowSetTitle(window, title);
    }
}
