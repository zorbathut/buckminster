using System.Runtime.InteropServices;

namespace Buckminster;

// The first hand-mirrored #[repr(C)] struct -- keep in sync with EngineConfig in src/core/rust/engine.rs; EngineLifecycleTests.EngineConfigLayoutMatchesRust asserts size and field offsets against the Rust exports, so drift fails tests instead of corrupting calls.
[StructLayout(LayoutKind.Sequential)]
public struct EngineConfig
{
    public int LogLevelMax;
    public uint LogBufferCapacity;
}
