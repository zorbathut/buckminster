//! The process-global engine lifecycle across the FFI (M5.75 ABI split: the old per-instance buck_engine_* surface split into this module's globals and demesne.rs's instances). `buck_globals_init`/`buck_globals_shutdown` bracket one engine cycle -- init configures the process-scoped log pipeline and resets the global state to virgin (which is also the poison-recovery path), shutdown marks it down; cycles within one process are the supported norm (the C# test suite runs one per test). The global tick counter lives here because ticking is a globals concern (one MainLoop, one sim clock); demesnes are render residency and do not tick.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Mutex;

use crate::ffi::{FfiCode, FfiError, buck_export, buck_struct, panic_message};

/// Globals init config. The log fields feed logging::configure (process-scoped, last-wins across cycles).
#[buck_struct(public)]
#[repr(C)]
#[derive(Clone, Copy)]
pub struct EngineConfig {
    /// Threshold for the buffered log channel delivered to the log sink (0 = off .. 5 = trace).
    pub log_level_max: i32,
    /// Capacity of the log record buffer; oldest records drop (loudly reported) when a host never drains. Must be at least 1.
    pub log_buffer_capacity: u32,
    /// Threshold for the immediate stderr echo (0 = off .. 5 = trace), independent of log_level_max: records clearing this go to stderr at emit time, before buffering -- the zero-latency, crash-proof diagnostics channel.
    pub log_stderr_level_max: i32,
}

struct Globals {
    initialized: bool,
    tick_count: u64,
    // Set when a panic was caught inside the globals scope: the state is suspect, so every subsequent globals op returns Poisoned -- except shutdown (teardown must keep working) and init (re-initializing IS the recovery path; C#'s wedge-then-Shutdown-then-Initialize flow lands here).
    poisoned: bool,
}

static GLOBALS: Mutex<Globals> = Mutex::new(Globals {
    initialized: false,
    tick_count: 0,
    poisoned: false,
});

// Every globals-scoped export goes through here; same containment discipline as demesne.rs's with_demesne (the doc there is the load-bearing one): the catch_unwind runs INSIDE the lock scope so a panic can never poison the std mutex, and the body must not invoke callbacks or re-enter any buck_* export while the lock is held.
fn with_globals<R>(body: impl FnOnce(&mut Globals) -> Result<R, FfiError>) -> Result<R, FfiError> {
    let mut globals = GLOBALS
        .lock()
        .expect("GLOBALS mutex poisoned: a panic escaped the globals-scoped containment");
    if !globals.initialized {
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            "the engine globals are not initialized; call buck_globals_init first",
        ));
    }
    if globals.poisoned {
        return Err(FfiError::new(
            FfiCode::Poisoned,
            "the engine globals are poisoned by an earlier panic; the state is suspect -- shut down and re-initialize",
        ));
    }
    match catch_unwind(AssertUnwindSafe(|| body(&mut globals))) {
        Ok(result) => result,
        Err(payload) => {
            globals.poisoned = true;
            Err(FfiError::new(FfiCode::Panic, panic_message(&payload)))
        }
    }
}

/// Initializes the engine globals for one cycle: configures the process-scoped log pipeline (last-wins) and resets the global state -- tick count to zero, poison cleared (re-initializing is the poison-recovery path). The C# facade guards against double-init; at this layer a re-init without shutdown is simply a reset, consistent with logging's last-wins.
#[buck_export]
fn globals_init(config: &EngineConfig) -> Result<(), FfiError> {
    // Config validation (including the capacity >= 1 rule) lives in logging::configure, the module that owns the constraint.
    crate::logging::configure(
        config.log_level_max,
        config.log_stderr_level_max,
        config.log_buffer_capacity,
    )?;
    let mut globals = GLOBALS
        .lock()
        .expect("GLOBALS mutex poisoned: a panic escaped the globals-scoped containment");
    *globals = Globals {
        initialized: true,
        tick_count: 0,
        poisoned: false,
    };
    Ok(())
}

/// Shuts the engine globals down. Deliberately works on poisoned and never-initialized globals alike (idempotent) -- teardown is a guarantee, not a privilege; this call's own exit-drain is what delivers the final buffered log tail through the still-registered sink.
#[buck_export]
fn globals_shutdown() {
    let mut globals = GLOBALS
        .lock()
        .expect("GLOBALS mutex poisoned: a panic escaped the globals-scoped containment");
    globals.initialized = false;
}

/// Advances the global tick and returns the new tick count.
#[buck_export(ret_names(tick_count))]
fn globals_tick(dt: f64) -> Result<u64, FfiError> {
    with_globals(|globals| {
        // dt is unused until a subsystem consumes it; the signature is the tick-as-callee contract from PLAN.md and is not going to churn per-milestone.
        let _ = dt;
        globals.tick_count += 1;
        Ok(globals.tick_count)
    })
}

/// Permanent test-only export: panics inside the globals scope, proving the GLOBAL poison latch (and its init-clears-it recovery) against the shipped artifact. Companion to buck_demesne_test_panic (per-instance poison) and buck_test_panic (bare containment).
#[buck_export]
fn globals_test_panic() -> Result<(), FfiError> {
    with_globals(|_globals| panic!("deliberate panic for globals poison testing"))
}
