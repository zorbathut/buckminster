//! Engine lifecycle across the FFI: `buck_engine_create`/`buck_engine_destroy` own an [`Engine`] that all future subsystems hang off (no globals beyond the unavoidable -- the ENGINES registry below and the process-scoped logger are the sanctioned two). The engine handle crossing the FFI is a real RID from the registry's allocator, so the allocator is exercised through genuine use from day one.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Mutex;

use crate::ffi::{FfiCode, FfiError, guard, panic_message};
use crate::rid::{Rid, RidAllocator, RidError};

/// The first hand-mirrored `#[repr(C)]` struct -- keep in sync with EngineConfig in src/core/cs/EngineConfig.cs; buck_layout_engine_config is the assertion seam that catches drift. The fields feed logging::configure at create (last-wins across engines; logging is process-scoped).
#[repr(C)]
#[derive(Clone, Copy)]
pub struct EngineConfig {
    pub log_level_max: i32,
    pub log_buffer_capacity: u32,
    pub log_stderr_level_max: i32,
}

pub struct Engine {
    pub tick_count: u64,
    // Set when a panic was caught inside this engine's scope: the state is suspect, so every subsequent op returns EnginePoisoned -- except destroy, which must keep working (explicit engine destroy is the teardown-ordering guarantee the callback lifetime story leans on).
    pub poisoned: bool,
}

const TAG_ENGINE: u8 = 1;

// Statics never drop, so the allocator's Drop leak report can't fire for this instance; engine leak detection needs its own explicit check if it's ever wanted at process exit.
static ENGINES: Mutex<RidAllocator<Engine>> = Mutex::new(RidAllocator::new(TAG_ENGINE));

fn invalid_handle_error(handle: u64, why: RidError) -> FfiError {
    FfiError::new(
        FfiCode::InvalidArgument,
        format!("invalid engine handle {handle:#x}: {why}"),
    )
}

// Every engine-scoped export goes through here. The inner catch_unwind runs INSIDE the lock scope: a panic in the body never unwinds through the MutexGuard (which would poison the std mutex and brick every later ENGINES.lock() in the process, destroy included); instead it marks this one engine poisoned and reports Panic through the normal rails.
//
// The body runs while the ENGINES lock is held: it must not invoke callbacks into C# (callback rule 4) and must not re-enter ANY buck_* export -- not just engine-scoped ones, because every export's exit-drain is a potential log-sink invocation, and invoking the sink under this lock is the rule-4 deadlock (and the mutex is not reentrant besides). This is the load-bearing constraint for M5 window callbacks and module hooks: work that calls out happens before the lock or after release.
//
// The lock() expect can only fire if a panic escaped this containment. It then unwinds into guard's outer catch_unwind, so every subsequent engine call returns Panic with this message (plus per-call stderr backtraces) -- degraded but loud, and it can't deadlock or alias state.
fn with_engine<R>(
    handle: u64,
    body: impl FnOnce(&mut Engine) -> Result<R, FfiError>,
) -> Result<R, FfiError> {
    let mut engines = ENGINES
        .lock()
        .expect("ENGINES mutex poisoned: a panic escaped the engine-scoped containment");
    let rid = Rid::from_raw(handle);
    let engine = match engines.get_mut(rid) {
        Some(engine) => engine,
        None => {
            return Err(FfiError::new(
                FfiCode::InvalidArgument,
                format!("invalid engine handle {handle:#x}: stale, destroyed, or never created"),
            ));
        }
    };
    if engine.poisoned {
        return Err(FfiError::new(
            FfiCode::EnginePoisoned,
            "engine is poisoned by an earlier panic; its state is suspect -- destroy it",
        ));
    }
    match catch_unwind(AssertUnwindSafe(|| body(engine))) {
        Ok(result) => result,
        Err(payload) => {
            engines
                .get_mut(rid)
                .expect("engine vanished under the held ENGINES lock")
                .poisoned = true;
            Err(FfiError::new(FfiCode::Panic, panic_message(&payload)))
        }
    }
}

/// # Safety
/// `config` must point to a valid EngineConfig; `out_engine` must be non-null and writable (the standard out-param contract).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn buck_engine_create(
    config: *const EngineConfig,
    out_engine: *mut u64,
) -> i32 {
    guard(|| {
        let config = unsafe { *config };
        // Config validation (including the capacity >= 1 rule) lives in logging::configure, the module that owns the constraint.
        crate::logging::configure(
            config.log_level_max,
            config.log_stderr_level_max,
            config.log_buffer_capacity,
        )?;
        let engine = Engine {
            tick_count: 0,
            poisoned: false,
        };
        let rid = ENGINES
            .lock()
            .expect("ENGINES mutex poisoned: a panic escaped the engine-scoped containment")
            .insert(engine);
        unsafe {
            *out_engine = rid.raw();
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn buck_engine_destroy(engine: u64) -> i32 {
    guard(|| {
        // Deliberately not with_engine: destroy must work on poisoned engines (it is the teardown-ordering guarantee), and remove gives the detailed RidError for the message.
        let mut engines = ENGINES
            .lock()
            .expect("ENGINES mutex poisoned: a panic escaped the engine-scoped containment");
        match engines.remove(Rid::from_raw(engine)) {
            Ok(_engine) => Ok(()),
            Err(why) => Err(invalid_handle_error(engine, why)),
        }
    })
}

/// # Safety
/// `out_tick_count` must be non-null and writable (the standard out-param contract).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn buck_engine_tick(engine: u64, dt: f64, out_tick_count: *mut u64) -> i32 {
    guard(|| {
        let tick_count = with_engine(engine, |engine| {
            // dt is unused until a subsystem consumes it (M5's window pump at the earliest); the signature is the tick-as-callee contract from PLAN.md and is not going to churn per-milestone.
            let _ = dt;
            engine.tick_count += 1;
            Ok(engine.tick_count)
        })?;
        unsafe {
            *out_tick_count = tick_count;
        }
        Ok(())
    })
}

/// Permanent test-only export: panics inside the engine scope, proving the poison mechanism against the shipped artifact (companion to buck_test_panic, which proves bare containment).
#[unsafe(no_mangle)]
pub extern "C" fn buck_engine_test_panic(engine: u64) -> i32 {
    guard(|| {
        with_engine(engine, |_engine| {
            panic!("deliberate panic for engine poison testing")
        })
    })
}

/// Permanent test-only export: logs two records then panics, proving the records-before-result guarantee against the shipped artifact -- the exit-drain must deliver both, in order, before the Panic code returns, and the engine must be poisoned afterward.
#[unsafe(no_mangle)]
pub extern "C" fn buck_engine_test_log_then_panic(engine: u64) -> i32 {
    guard(|| {
        with_engine(engine, |_engine| {
            log::error!("first record before the panic");
            log::error!("second record before the panic");
            panic!("deliberate panic after logging")
        })
    })
}

/// The layout-assertion seam for EngineConfig (one export per mirrored struct, one out-param per field): C# compares against Marshal.SizeOf/OffsetOf, the interim protection until FFI binding generation exists.
///
/// # Safety
/// All out-params must be non-null and writable (the standard out-param contract).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn buck_layout_engine_config(
    out_size: *mut u64,
    out_offset_log_level_max: *mut u64,
    out_offset_log_buffer_capacity: *mut u64,
    out_offset_log_stderr_level_max: *mut u64,
) -> i32 {
    guard(|| {
        unsafe {
            *out_size = std::mem::size_of::<EngineConfig>() as u64;
            *out_offset_log_level_max = std::mem::offset_of!(EngineConfig, log_level_max) as u64;
            *out_offset_log_buffer_capacity =
                std::mem::offset_of!(EngineConfig, log_buffer_capacity) as u64;
            *out_offset_log_stderr_level_max =
                std::mem::offset_of!(EngineConfig, log_stderr_level_max) as u64;
        }
        Ok(())
    })
}
