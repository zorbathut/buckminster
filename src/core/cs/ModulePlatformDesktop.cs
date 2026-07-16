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
        FfiCall.ThrowOnError(NativeMethods.buck_platform_pump(), "platform init pump");
    }

    public void PumpEvents(Engine engine)
    {
    }

    public void Tick(Engine engine, double dt)
    {
    }

    internal void Pump()
    {
        FfiCall.ThrowOnError(NativeMethods.buck_platform_pump(), "platform pump");
    }

    internal unsafe (uint Written, uint Remaining) Poll(Span<PlatformEventRaw> buffer)
    {
        fixed (PlatformEventRaw* pointer = buffer)
        {
            FfiCall.ThrowOnError(NativeMethods.buck_platform_events_poll(pointer, (uint)buffer.Length, out uint written, out uint remaining), "platform events poll");
            return (written, remaining);
        }
    }

    internal ulong CreateNativeWindow(string title, uint width, uint height)
    {
        FfiCall.ThrowOnError(NativeMethods.WindowCreate(title, width, height, out ulong window), "window create");
        return window;
    }

    internal void DestroyNativeWindow(ulong window)
    {
        FfiCall.ThrowOnError(NativeMethods.buck_window_destroy(window), "window destroy");
    }
}
