//! The Rust->C# log pipeline: the `log` crate's records buffer in a process-global queue (logging is process-scoped -- the log crate's logger is a process global, and multi-engine log separation is a non-goal), drained to the C# sink at a safe point via [`buck_logs_drain`]. Buffering rather than a synchronous callback per statement is what keeps callback rule 4 honest: a `log::info!` deep inside borrowed engine state must not re-enter C#.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Mutex, Once};

use crate::ffi::{FfiCode, FfiError, guard};

/// The sink shape mirrored by C#'s `delegate* unmanaged<ulong, int, byte*, nuint, int>` (crossing the import as IntPtr, per the wasm binding constraint). The message span is valid only for the duration of the call.
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
// Read on every log statement, written by every engine create (last-wins, like the level filter).
static CAPACITY: AtomicU32 = AtomicU32::new(0);
static INSTALL: Once = Once::new();
static LOGGER: LoggerBuck = LoggerBuck;

// The i32s crossing the FFI are the log crate's Level discriminants; LogLevel.cs mirrors them. The crate pins Error=1 in source but not as documented API -- this makes the mirror compiler-enforced.
const _: () = assert!(
    log::Level::Error as i32 == 1
        && log::Level::Warn as i32 == 2
        && log::Level::Info as i32 == 3
        && log::Level::Debug as i32 == 4
        && log::Level::Trace as i32 == 5
);

struct LoggerBuck;

impl log::Log for LoggerBuck {
    fn enabled(&self, _metadata: &log::Metadata) -> bool {
        // Filtering happens via log::set_max_level (cheaper: checked before the record is even built).
        true
    }

    fn log(&self, record: &log::Record) {
        // Format BEFORE taking the lock: a panicking Display impl must not poison the process-global buffer mutex (this global has no poison-containment machinery, unlike ENGINES).
        let message = record.args().to_string();
        let mut buffer = BUFFER.lock().expect("log buffer mutex poisoned");
        let capacity = CAPACITY.load(Ordering::Relaxed) as usize;
        while buffer.records.len() >= capacity {
            // Drop-oldest and count: the drain reports the loss loudly instead of the buffer growing without bound under a host that never pumps.
            buffer.records.pop_front();
            buffer.dropped += 1;
        }
        buffer.records.push_back((record.level() as i32, message));
    }

    fn flush(&self) {}
}

/// Called by every buck_engine_create: installs the logger once, then re-applies filter and capacity (last-wins -- first-wins would make any multi-engine process, including the whole test corpus, order-dependent on which engine was created first).
pub fn configure(log_level_max: i32, log_buffer_capacity: u32) -> Result<(), FfiError> {
    if log_buffer_capacity == 0 {
        // Validated here, at this module's own seam, because a zero capacity reaching the log path is not just lossy -- the drop-oldest loop on an empty deque would spin forever holding the buffer lock.
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            "log_buffer_capacity must be at least 1 (a zero-capacity buffer would drop every record)",
        ));
    }
    let filter = match log_level_max {
        0 => log::LevelFilter::Off,
        1 => log::LevelFilter::Error,
        2 => log::LevelFilter::Warn,
        3 => log::LevelFilter::Info,
        4 => log::LevelFilter::Debug,
        5 => log::LevelFilter::Trace,
        _ => {
            return Err(FfiError::new(
                FfiCode::InvalidArgument,
                format!("log_level_max must be 0 (off) through 5 (trace), got {log_level_max}"),
            ));
        }
    };
    CAPACITY.store(log_buffer_capacity, Ordering::Relaxed);
    INSTALL.call_once(|| {
        log::set_logger(&LOGGER)
            .expect("a logger was already installed by something else in the process");
    });
    log::set_max_level(filter);
    Ok(())
}

/// Swaps the buffered records out under the lock, then invokes the sink once per record OUTSIDE it (callback rule 4: no lock held across a callback). If the sink fails mid-drain, the failed record is lost WITH the sink's error as its loud marker (redelivering it would make a deterministically-throwing sink a poison pill wedging every future pump), and the undelivered tail is pushed back to the buffer front for the next drain -- a failing sink must not silently eat the queue. Exception to the lost-record rule: the synthesized drop-report is never lost -- its count stays in the buffer until a delivery succeeds, because losing the "N records were lost" record would defeat the loudness invariant itself.
#[unsafe(no_mangle)]
pub extern "C" fn buck_logs_drain(sink: LogSinkFn, userdata: u64) -> i32 {
    guard(|| {
        let (mut records, dropped) = {
            let mut buffer = BUFFER.lock().expect("log buffer mutex poisoned");
            let records = std::mem::take(&mut buffer.records);
            // Deliberately read, not reset: the count clears only after the synthesized report is successfully delivered.
            (records, buffer.dropped)
        };
        if dropped > 0 {
            records.push_front((
                log::Level::Warn as i32,
                format!("{dropped} log record(s) dropped: buffer capacity exceeded before drain"),
            ));
        }
        let mut drop_report_pending = dropped > 0;
        while let Some((level, message)) = records.pop_front() {
            let code = sink(userdata, level, message.as_ptr(), message.len());
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
    })
}

/// Permanent test-only export: emits through the real `log::log!` path so the pipeline tests exercise the actual machinery, deterministically.
///
/// # Safety
/// `msg` must point to `msg_len` readable bytes of valid UTF-8 when `msg_len > 0` (invalid UTF-8 is rejected as an error, not UB). `msg` MAY be null when `msg_len` is 0: C#'s `fixed` on an empty array pins null, and `from_raw_parts(null, 0)` would be a non-unwinding abort that no guard can contain -- the empty case is handled without touching the pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn buck_test_log(level: i32, msg: *const u8, msg_len: usize) -> i32 {
    guard(|| {
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
        let bytes: &[u8] = if msg_len == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(msg, msg_len) }
        };
        let text = std::str::from_utf8(bytes).map_err(|error| {
            FfiError::new(
                FfiCode::InvalidArgument,
                format!("log message is not valid UTF-8: {error}"),
            )
        })?;
        log::log!(level, "{text}");
        Ok(())
    })
}
