namespace Buckminster;

// Mirrors the Rust log crate's Level values -- keep in sync with the level integers crossing buck_logs_drain (src/core/rust/logging.rs). 0 is reserved for "off" in EngineConfig.LogLevelMax and never appears on a record.
public enum LogLevel
{
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}
