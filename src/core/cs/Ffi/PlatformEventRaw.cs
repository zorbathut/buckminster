using System.Runtime.InteropServices;

namespace Buckminster.Ffi;

// Hand-mirrored from PlatformEventRaw in src/core/rust/platform.rs -- keep in sync; buck_layout_platform_event is the assertion seam that catches drift. Field order is the Rust side's deliberate padding-free layout (u64 first, 24 bytes). Data0..Data2 are per-kind, documented on the Rust struct.
[StructLayout(LayoutKind.Sequential)]
internal struct PlatformEventRaw
{
    internal ulong Window;
    internal int Kind;
    internal uint Data0;
    internal uint Data1;
    internal uint Data2;
}
