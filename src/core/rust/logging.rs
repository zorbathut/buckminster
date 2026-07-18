//! The unified log pipeline, both languages through one path: Rust code logs via the `log` crate, C# logs via the `Log` facade over [`buck_log`] -- one buffer, one ordering, one filter, one echo channel, one delivery path. Two delivery channels: the immediate channel (stderr today; the seam for future network error reporting or editor-over-socket) fires at emit time, crash-proof and zero-latency; the registered log sink (C#) is delivered by the exit-drain at the tail of every `buck_*` call (ffi::guard), where every lock and borrow is provably released but the causal C# call is still on the stack. Buffering rather than a synchronous callback per statement is what keeps callback rule 4 honest: a `log::info!` deep inside borrowed engine state must not re-enter C#.
//!
//! Logging is process-scoped (the log crate's logger is a process global; multi-engine separation is a non-goal): config and sink registration are last-wins across engine creates.

use std::cell::Cell;
use std::collections::VecDeque;
use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU32, Ordering};
use std::sync::{Mutex, Once};

use crate::ffi::{FfiCode, FfiError, buck_enum, buck_export};

/// The log-sink shape mirrored by C#'s `delegate* unmanaged<ulong, int, byte*, nuint, int>` (crossing the import as IntPtr, per the wasm binding constraint). The message span is valid only for the duration of the call.
pub type LogSinkFn =
    extern "C" fn(userdata: u64, level: i32, msg: *const u8, msg_len: usize) -> i32;

struct LogBuffer {
    records: VecDeque<(i32, String)>,
    dropped: u64,
}

static BUFFER: Mutex<LogBuffer> = Mutex::new(LogBuffer {
    records: VecDeque::new(),
    dropped: 0,
});
static LOG_SINK: Mutex<Option<(LogSinkFn, u64)>> = Mutex::new(None);
// Per-channel thresholds and capacity, read on every log statement, written by every engine create (last-wins). Two independent thresholds: the log crate's single global max_level is set to the max of both, and each channel filters itself here -- otherwise configuring {buffer: Error, stderr: Trace} would silently cap stderr at Error.
static LEVEL_BUFFER: AtomicI32 = AtomicI32::new(0);
static LEVEL_STDERR: AtomicI32 = AtomicI32::new(0);
static CAPACITY: AtomicU32 = AtomicU32::new(0);
// Distinguishes "never configured" (buck_log fails loudly -- a silent-success Log.Error before any engine exists is unacceptable) from configured-Off (legitimate, written-down silence).
static CONFIGURED: AtomicBool = AtomicBool::new(false);
static INSTALL: Once = Once::new();
static LOGGER: LoggerBuck = LoggerBuck;

thread_local! {
    // True while the exit-drain is delivering on this thread: nested buck_* calls made by the log sink skip their own exit-drain (no recursion, no livelock; their records wait for the next exit). Managed only by DrainScopeGuard -- a panic leaving this set would silently kill all future delivery.
    static DRAINING: Cell<bool> = const { Cell::new(false) };
}

struct DrainScopeGuard;

impl DrainScopeGuard {
    fn enter() -> Option<DrainScopeGuard> {
        DRAINING.with(|flag| {
            if flag.get() {
                None
            } else {
                flag.set(true);
                Some(DrainScopeGuard)
            }
        })
    }
}

impl Drop for DrainScopeGuard {
    fn drop(&mut self) {
        DRAINING.with(|flag| flag.set(false));
    }
}

/// Log severity as it crosses the FFI: the log crate's Level values. 0 is reserved for "off" in the EngineConfig thresholds and never appears on a record. Exists as a Rust enum to be the generated C# mirror's source of truth; Rust code itself logs through the log crate's own Level.
#[buck_enum(public)]
#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum LogLevel {
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

// The i32s crossing the FFI are the log crate's Level discriminants; the LogLevel mirror above and the generated C# enum carry them. The crate pins Error=1 in source but not as documented API -- this makes the mirror compiler-enforced.
const _: () = assert!(
    log::Level::Error as i32 == LogLevel::Error as i32
        && log::Level::Warn as i32 == LogLevel::Warn as i32
        && log::Level::Info as i32 == LogLevel::Info as i32
        && log::Level::Debug as i32 == LogLevel::Debug as i32
        && log::Level::Trace as i32 == LogLevel::Trace as i32
);

// The immediate channel: fires at emit time, before buffering -- zero latency and crash-proof (the record is out before the log statement returns; a hard crash one instruction later can't lose it). stderr is today's only implementation; future channels (network error reporting, editor-over-socket) attach here. The write failure is deliberately discarded: the diagnostics channel of last resort discarding its own I/O failure is the one legitimate carve-out from the silent-error ban -- a panicking write (eprintln!) would fire per log line in a host with a dead stderr, poisoning engines from inside engine-scoped bodies.
fn channel_immediate(level: i32, message: &str) {
    let _ = writeln!(std::io::stderr(), "[buck:{level}] {message}");
}

struct LoggerBuck;

impl log::Log for LoggerBuck {
    fn enabled(&self, _metadata: &log::Metadata) -> bool {
        // The global max_level gate (max of both channel thresholds) runs before this; per-channel filtering happens in log().
        true
    }

    fn log(&self, record: &log::Record) {
        let level = record.level() as i32;
        // Format BEFORE taking the lock: a panicking Display impl must not poison the process-global buffer mutex (this global has no poison-containment machinery, unlike ENGINES).
        let message = record.args().to_string();
        if level <= LEVEL_STDERR.load(Ordering::Relaxed) {
            channel_immediate(level, &message);
        }
        if level <= LEVEL_BUFFER.load(Ordering::Relaxed) {
            let mut buffer = BUFFER.lock().expect("log buffer mutex poisoned");
            let capacity = CAPACITY.load(Ordering::Relaxed) as usize;
            while buffer.records.len() >= capacity {
                // Drop-oldest and count: the drain reports the loss loudly instead of the buffer growing without bound under a host that never calls in.
                buffer.records.pop_front();
                buffer.dropped += 1;
            }
            buffer.records.push_back((level, message));
        }
    }

    fn flush(&self) {}
}

fn level_filter(value: i32, field: &str) -> Result<log::LevelFilter, FfiError> {
    match value {
        0 => Ok(log::LevelFilter::Off),
        1 => Ok(log::LevelFilter::Error),
        2 => Ok(log::LevelFilter::Warn),
        3 => Ok(log::LevelFilter::Info),
        4 => Ok(log::LevelFilter::Debug),
        5 => Ok(log::LevelFilter::Trace),
        _ => Err(FfiError::new(
            FfiCode::InvalidArgument,
            format!("{field} must be 0 (off) through 5 (trace), got {value}"),
        )),
    }
}

/// Called by every buck_engine_create: installs the logger once, then re-applies thresholds and capacity (last-wins -- first-wins would make any multi-engine process, including the whole test corpus, order-dependent on which engine was created first).
pub fn configure(
    log_level_max: i32,
    log_stderr_level_max: i32,
    log_buffer_capacity: u32,
) -> Result<(), FfiError> {
    if log_buffer_capacity == 0 {
        // Validated here, at this module's own seam, because a zero capacity reaching the log path is not just lossy -- the drop-oldest loop on an empty deque would spin forever holding the buffer lock.
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            "log_buffer_capacity must be at least 1 (a zero-capacity buffer would drop every record)",
        ));
    }
    let buffer_filter = level_filter(log_level_max, "log_level_max")?;
    let stderr_filter = level_filter(log_stderr_level_max, "log_stderr_level_max")?;
    LEVEL_BUFFER.store(log_level_max, Ordering::Relaxed);
    LEVEL_STDERR.store(log_stderr_level_max, Ordering::Relaxed);
    CAPACITY.store(log_buffer_capacity, Ordering::Relaxed);
    INSTALL.call_once(|| {
        log::set_logger(&LOGGER)
            .expect("a logger was already installed by something else in the process");
    });
    log::set_max_level(buffer_filter.max(stderr_filter));
    CONFIGURED.store(true, Ordering::Relaxed);
    Ok(())
}

/// The exit-drain, called from ffi::guard at the tail of every export: deliver buffered records to the registered log sink, records-before-result, unconditionally (a routine error return or a contained panic deserves its diagnostics MORE, not less). Returns Ok(()) when there is nothing to do (no sink, empty buffer, or already draining on this thread).
///
/// The registration is copied out of its mutex and both locks are released before any sink invocation (callback rule 4). Snapshot-only: records logged by the sink itself wait for the next exit. If the sink fails mid-drain, the failed record is lost WITH the sink's error as its loud marker (redelivering it would make a deterministically-throwing sink a poison pill wedging every future call), and the undelivered tail is pushed back to the buffer front. Exception: the synthesized drop-report's count stays in the buffer until a delivery succeeds -- losing the "N records never reached the log sink" record would defeat the loudness invariant itself.
pub fn drain_at_exit() -> Result<(), FfiError> {
    let Some(_scope) = DrainScopeGuard::enter() else {
        return Ok(());
    };
    let Some((log_sink, userdata)) = *LOG_SINK.lock().expect("log sink mutex poisoned") else {
        return Ok(());
    };
    let (mut records, dropped) = {
        let mut buffer = BUFFER.lock().expect("log buffer mutex poisoned");
        let records = std::mem::take(&mut buffer.records);
        // Deliberately read, not reset: the count clears only after the synthesized report is successfully delivered.
        (records, buffer.dropped)
    };
    if records.is_empty() && dropped == 0 {
        return Ok(());
    }
    if dropped > 0 {
        records.push_front((log::Level::Warn as i32, format!("{dropped} log record(s) never reached the log sink: buffer capacity exceeded (stderr may have carried them)")));
    }
    let mut drop_report_pending = dropped > 0;
    while let Some((level, message)) = records.pop_front() {
        let code = log_sink(userdata, level, message.as_ptr(), message.len());
        if code != 0 {
            let mut buffer = BUFFER.lock().expect("log buffer mutex poisoned");
            while let Some(record) = records.pop_back() {
                buffer.records.push_front(record);
            }
            return Err(FfiError::new(
                FfiCode::CallbackError,
                format!("log sink returned error code {code}"),
            ));
        }
        if drop_report_pending {
            // The report landed; clear exactly what it reported (drops accrued during this drain keep accumulating for the next one).
            drop_report_pending = false;
            BUFFER.lock().expect("log buffer mutex poisoned").dropped -= dropped;
        }
    }
    Ok(())
}

/// Registers the process-wide log sink (last-wins, like all logging config). Clearing is a separate export because a null fn pointer is not an expressible LogSinkFn.
#[unsafe(no_mangle)]
pub extern "C" fn buck_log_sink_set(log_sink: LogSinkFn, userdata: u64) -> i32 {
    crate::ffi::guard(|| {
        *LOG_SINK.lock().expect("log sink mutex poisoned") = Some((log_sink, userdata));
        Ok(())
    })
}

/// Clears the log sink registration. This call's own exit-drain sees no sink and delivers nothing -- the last delivery point was the preceding call's exit; a stranded pushback tail stays buffered (stderr already carried it) until a new registration.
#[unsafe(no_mangle)]
pub extern "C" fn buck_log_sink_clear() -> i32 {
    crate::ffi::guard(|| {
        *LOG_SINK.lock().expect("log sink mutex poisoned") = None;
        Ok(())
    })
}

/// The C#-logging entry point (the `Log` facade's other half): emits through the real `log::log!` path, so C# records take exactly the pipeline Rust records do -- same buffer, same ordering, same thresholds, same channels. Also serves the pipeline tests as their deterministic injection probe.
#[buck_export]
fn log(level: i32, msg: &str) -> Result<(), FfiError> {
    if !CONFIGURED.load(Ordering::Relaxed) {
        // Before any engine has configured logging, even the stderr echo is dead (the global max_level defaults to Off) -- a silent-success Log.Error would be unacceptable from a facade whose point is loudness.
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            "logging is not configured; create an engine first (Engine.Create installs and configures the log pipeline)",
        ));
    }
    let level = match level {
        1 => log::Level::Error,
        2 => log::Level::Warn,
        3 => log::Level::Info,
        4 => log::Level::Debug,
        5 => log::Level::Trace,
        _ => {
            return Err(FfiError::new(
                FfiCode::InvalidArgument,
                format!("log level must be 1 (error) through 5 (trace), got {level}"),
            ));
        }
    };
    log::log!(level, "{msg}");
    Ok(())
}
