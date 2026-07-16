using System;

namespace Buckminster;

// The window event vocabulary: plain data, host-injectable by design -- ModulePlatformDesktop is merely the desktop producer, tests inject directly, and the future recorded-input trace replays through the same shapes (PLAN.md determinism rules: events are recorded input). Kind values mirror the Rust EVENT_KIND_* constants in src/core/rust/platform.rs.

public enum WindowEventKind
{
    Resized = 1,
    CloseRequested = 2,
    FocusChanged = 3,
    Key = 4,
}

// Mirrors the Rust MODIFIER_* bits in src/core/rust/platform.rs.
[Flags]
public enum KeyModifiers : uint
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Meta = 8,
}

public readonly struct KeyEventData
{
    public KeyCode Code { get; }
    public bool Pressed { get; }
    // True for OS auto-repeat while the key is held -- a flag on the same event, not a separate kind (Godot's echo).
    public bool Repeat { get; }
    public KeyModifiers Modifiers { get; }

    public KeyEventData(KeyCode code, bool pressed, bool repeat, KeyModifiers modifiers)
    {
        Code = code;
        Pressed = pressed;
        Repeat = repeat;
        Modifiers = modifiers;
    }
}

// One event as injectable data: a kind plus three raw words whose meaning is per-kind (matching the wire shape, so a recorded trace is trivially this). Construct through the per-kind factories; ModuleWindow decodes when dispatching.
public readonly struct WindowEventData
{
    public WindowEventKind Kind { get; }
    public uint Data0 { get; }
    public uint Data1 { get; }
    public uint Data2 { get; }

    private WindowEventData(WindowEventKind kind, uint data0, uint data1, uint data2)
    {
        Kind = kind;
        Data0 = data0;
        Data1 = data1;
        Data2 = data2;
    }

    public static WindowEventData Resized(uint width, uint height)
    {
        return new WindowEventData(WindowEventKind.Resized, width, height, 0);
    }

    public static WindowEventData CloseRequested()
    {
        return new WindowEventData(WindowEventKind.CloseRequested, 0, 0, 0);
    }

    public static WindowEventData FocusChanged(bool focused)
    {
        return new WindowEventData(WindowEventKind.FocusChanged, focused ? 1u : 0u, 0, 0);
    }

    public static WindowEventData Key(KeyCode code, bool pressed, bool repeat, KeyModifiers modifiers)
    {
        // Flag bits mirror the Rust KEY_FLAG_* constants.
        uint flags = (pressed ? 1u : 0u) | (repeat ? 2u : 0u);
        return new WindowEventData(WindowEventKind.Key, (uint)code, flags, (uint)modifiers);
    }

    // The wire side of the same shape: what ModuleWindow builds from a polled PlatformEventRaw. An unknown kind flows through and fails loudly at dispatch (it can only come from our own Rust, so it is a real bug, not input).
    internal static WindowEventData FromRaw(int kind, uint data0, uint data1, uint data2)
    {
        return new WindowEventData((WindowEventKind)kind, data0, data1, data2);
    }
}
