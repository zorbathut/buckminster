using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Buckminster;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

// The C#<->Rust sync checks for the platform vocabulary. The KeyCode mirror is verified EXHAUSTIVELY against the Rust name table -- every member, both directions, plus counts -- because a transposition in the middle of ~195 hand-mirrored entries would otherwise produce wrong-key events forever and a spot-check would sail past it. PlatformEventRaw joins the layout-assertion seam. All of this runs on every cell: the probe and layout exports live outside the platform stubs.
[TestFixture]
public class PlatformInteropTests
{
    [Test]
    public unsafe void KeyCodeMirrorsTheRustEnumExhaustively()
    {
        Assert.That(NativeMethods.buck_test_keycode_count(out uint rustCount), Is.EqualTo(FfiCode.Ok));
        KeyCode[] values = Enum.GetValues<KeyCode>();
        Assert.That((uint)values.Length, Is.EqualTo(rustCount), "member counts must match both ways -- an extra or missing entry on either side fails here");
        HashSet<uint> seenValues = new HashSet<uint>();
        byte* buf = stackalloc byte[64];
        foreach (KeyCode code in values)
        {
            Assert.That(seenValues.Add((uint)code), Is.True, $"duplicate numeric value {(uint)code} in the C# enum");
            Assert.That(NativeMethods.buck_test_keycode_name((uint)code, buf, 64, out nuint length), Is.EqualTo(FfiCode.Ok), $"Rust has no keycode with value {(uint)code} ({code})");
            string rustName = Encoding.UTF8.GetString(buf, checked((int)length));
            Assert.That(code.ToString(), Is.EqualTo(rustName), $"value {(uint)code}: C# name {code} vs Rust name {rustName}");
        }
        // And a value beyond the enum must be loudly absent, not quietly named.
        byte* probe = stackalloc byte[64];
        Assert.That(NativeMethods.buck_test_keycode_name(rustCount, probe, 64, out _), Is.EqualTo(FfiCode.InvalidArgument));
    }

    [Test]
    public void PlatformEventLayoutMatchesRust()
    {
        Assert.That(NativeMethods.buck_layout_platform_event(out uint size, out uint offsetWindow, out uint offsetKind, out uint offsetData0, out uint offsetData1, out uint offsetData2), Is.EqualTo(FfiCode.Ok));
        Assert.That((uint)Marshal.SizeOf<PlatformEventRaw>(), Is.EqualTo(size));
        Assert.That((uint)(long)Marshal.OffsetOf<PlatformEventRaw>(nameof(PlatformEventRaw.Window)), Is.EqualTo(offsetWindow));
        Assert.That((uint)(long)Marshal.OffsetOf<PlatformEventRaw>(nameof(PlatformEventRaw.Kind)), Is.EqualTo(offsetKind));
        Assert.That((uint)(long)Marshal.OffsetOf<PlatformEventRaw>(nameof(PlatformEventRaw.Data0)), Is.EqualTo(offsetData0));
        Assert.That((uint)(long)Marshal.OffsetOf<PlatformEventRaw>(nameof(PlatformEventRaw.Data1)), Is.EqualTo(offsetData1));
        Assert.That((uint)(long)Marshal.OffsetOf<PlatformEventRaw>(nameof(PlatformEventRaw.Data2)), Is.EqualTo(offsetData2));
    }
}
