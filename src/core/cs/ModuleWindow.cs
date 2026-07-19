using System;
using System.Collections.Generic;
using Buckminster.Ffi;

namespace Buckminster;

// The window abstraction module (PLAN.md M5, the owner's layering: a module that RELIES ON platform-specific modules). With a platform module wired, windows are native-backed and this module orchestrates the platform pump each engine PumpEvents; without one, windows are virtual and events arrive only by injection -- headless composition for free, and the same injection entry the future recorded-input trace replays through. Multiple windows are supported structurally; nothing defaults to creating one.
public sealed class ModuleWindow : IModule
{
    private readonly ModulePlatformDesktop? platform;
    private readonly List<Window> windows = new List<Window>();
    private readonly Dictionary<ulong, Window> byNativeRid = new Dictionary<ulong, Window>();
    private uint nextWindowId = 1;

    // The platform is conceptually optional with a principled absent value: null = headless/virtual. Known edge, documented rather than machined away: the dependency below is validated by TYPE, so a platform instance that was never registered would pass validation while wired here -- forgetting to register fails loudly via the missing-dependency error, the wrong-instance case waits for a registry lookup API.
    public ModuleWindow(ModulePlatformDesktop? platform = null)
    {
        this.platform = platform;
    }

    public Type[] Dependencies
    {
        get { return platform != null ? new[] { platform.GetType() } : Type.EmptyTypes; }
    }

    public void Initialize()
    {
    }

    public void Shutdown()
    {
        // Destroy whatever is still live: an exit queued by anything other than a cooperative close would otherwise leak native windows until process exit. Destroy is idempotent and removes from the list, hence the copy.
        foreach (Window window in windows.ToArray())
        {
            window.Destroy();
        }
    }

    public void PumpEvents()
    {
        if (platform == null)
        {
            return;
        }
        // The window module orchestrates updates (owner's design): one platform pump, then drain the buffered events into per-window dispatch.
        platform.Pump();
        Span<PlatformEventRaw> buffer = stackalloc PlatformEventRaw[32];
        while (true)
        {
            (uint written, uint remaining) = platform.Poll(buffer);
            for (int i = 0; i < written; i++)
            {
                DispatchRaw(in buffer[i]);
            }
            if (remaining == 0)
            {
                break;
            }
        }
    }

    public void Tick(double dt)
    {
    }

    public Window CreateWindow(string title, uint width, uint height)
    {
        ulong nativeRid = 0;
        if (platform != null)
        {
            nativeRid = platform.CreateNativeWindow(title, width, height);
        }
        Window window = new Window(this, nextWindowId, nativeRid, title, width, height);
        nextWindowId += 1;
        windows.Add(window);
        if (nativeRid != 0)
        {
            byNativeRid.Add(nativeRid, window);
        }
        return window;
    }

    // The single entry for event data: the platform producer resolves-and-drops unknown windows before calling this, tests and future trace replay call it directly. Injecting into a destroyed window is a caller bug.
    public void InjectEvent(Window window, in WindowEventData data)
    {
        if (window.IsDestroyed)
        {
            throw new InvalidOperationException($"cannot inject an event into destroyed window {window.Id}");
        }
        window.Dispatch(in data);
    }

    internal void SetWindowTitle(Window window, string title)
    {
        if (window.NativeRid != 0)
        {
            Native.WindowSetTitle(window.NativeRid, title);
        }
    }

    internal void OnWindowDestroyed(Window window)
    {
        windows.Remove(window);
        if (window.NativeRid != 0)
        {
            byNativeRid.Remove(window.NativeRid);
            platform!.DestroyNativeWindow(window.NativeRid);
        }
    }

    private void DispatchRaw(in PlatformEventRaw raw)
    {
        if (!byNativeRid.TryGetValue(raw.Window, out Window? window))
        {
            // Queued native events can outlive an explicit destroy by a pump; written-down drop, never silent.
            Log.Debug($"platform event kind {raw.Kind} for unknown native window {raw.Window:x} dropped (destroyed last frame?)");
            return;
        }
        // Through InjectEvent, not a private shortcut: the single-entry claim is the determinism seam's load-bearing invariant (recorded traces must flow exactly the path live events flow), and the destroyed-check is a free assertion here (a destroyed window is never in byNativeRid).
        InjectEvent(window, WindowEventData.FromRaw(raw.Kind, raw.Data0, raw.Data1, raw.Data2));
    }
}
