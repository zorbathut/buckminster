namespace Buckminster;

// Mirrors the Rust log crate's Level values -- keep in sync with the level integers crossing the log pipeline (src/core/rust/logging.rs; a const assert there pins the Rust side). 0 is reserved for "off" in the EngineConfig thresholds and never appears on a record.
public enum LogLevel
{
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}
