using System;

namespace Buckminster;

// A window as ModuleWindow manages it: platform-backed (native RID set) or virtual (no native backing -- headless tests and the recorded-trace future). Id is the stable per-module creation-order index that injected/recorded events name a window by; the native RID is a platform detail. Close is cooperative: CloseRequested is advisory and Destroy is the explicit teardown -- calling it from inside a CloseRequested handler is the expected idiom and works.
public sealed class Window
{
    private readonly ModuleWindow module;
    internal ulong NativeRid { get; }

    public uint Id { get; }
    public string Title { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public bool IsDestroyed { get; private set; }

    public event Action<Window, uint, uint>? Resized;
    public event Action<Window>? CloseRequested;
    public event Action<Window, KeyEventData>? Key;
    public event Action<Window, bool>? FocusChanged;

    internal Window(ModuleWindow module, uint id, ulong nativeRid, string title, uint width, uint height)
    {
        this.module = module;
        Id = id;
        NativeRid = nativeRid;
        Title = title;
        Width = width;
        Height = height;
    }

    public void SetTitle(string title)
    {
        ThrowIfDestroyed();
        module.SetWindowTitle(this, title);
        Title = title;
    }

    // Idempotent, and legal from inside this window's own event handlers (dispatch targets one window and never iterates a collection this mutates).
    public void Destroy()
    {
        if (IsDestroyed)
        {
            return;
        }
        IsDestroyed = true;
        module.OnWindowDestroyed(this);
    }

    internal void Dispatch(in WindowEventData data)
    {
        switch (data.Kind)
        {
            case WindowEventKind.Resized:
                // The window's own size updates before the event fires, so handlers reading window.Width see the new value.
                Width = data.Data0;
                Height = data.Data1;
                Resized?.Invoke(this, data.Data0, data.Data1);
                break;
            case WindowEventKind.CloseRequested:
                CloseRequested?.Invoke(this);
                break;
            case WindowEventKind.FocusChanged:
                FocusChanged?.Invoke(this, data.Data0 != 0);
                break;
            case WindowEventKind.Key:
                Key?.Invoke(this, new KeyEventData((KeyCode)data.Data0, (data.Data1 & 1) != 0, (data.Data1 & 2) != 0, (KeyModifiers)data.Data2));
                break;
            default:
                throw new InvalidOperationException($"unknown window event kind {(int)data.Kind}");
        }
    }

    private void ThrowIfDestroyed()
    {
        if (IsDestroyed)
        {
            throw new InvalidOperationException($"window {Id} is destroyed");
        }
    }
}
