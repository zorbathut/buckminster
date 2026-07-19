//! Demesne lifecycle across the FFI: the multi-instantiable unit of render residency (device/resources/window attachments arrive at M6; the struct is deliberately near-empty until then -- this chunk's job is the registry, the handles, and the per-instance poison containment, all inherited from the retired per-instance engine). Simulation is further up the stack and is not a demesne concern. The handle crossing the FFI is a real RID from the registry's allocator, so stale and reused-slot handles fail loudly.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Mutex;

use crate::ffi::{FfiCode, FfiError, buck_export, panic_message};
use crate::rid::{Rid, RidAllocator, RidError};

pub struct Demesne {
    // Set when a panic was caught inside this demesne's scope: the state is suspect, so every subsequent op returns Poisoned -- except destroy, which must keep working (explicit destroy is the teardown-ordering guarantee the callback lifetime story leans on).
    pub poisoned: bool,
}

// The RID tag registry, one comment, every crate: 1 = demesne (core, here); 2 = window (buckminster-host-desktop, platform.rs). Tags are process-unique by convention -- a new allocator claims the next number and extends this list.
const TAG_DEMESNE: u8 = 1;

// Statics never drop, so the allocator's Drop leak report can't fire for this instance; demesne leak detection needs its own explicit check if it's ever wanted at process exit.
static DEMESNES: Mutex<RidAllocator<Demesne>> = Mutex::new(RidAllocator::new(TAG_DEMESNE));

fn invalid_handle_error(handle: Rid, why: RidError) -> FfiError {
    let raw = handle.raw();
    FfiError::new(
        FfiCode::InvalidArgument,
        format!("invalid demesne handle {raw:#x}: {why}"),
    )
}

// Every demesne-scoped export goes through here. The inner catch_unwind runs INSIDE the lock scope: a panic in the body never unwinds through the MutexGuard (which would poison the std mutex and brick every later DEMESNES.lock() in the process, destroy included); instead it marks this one demesne poisoned and reports Panic through the normal rails.
//
// The body runs while the DEMESNES lock is held: it must not invoke callbacks into C# (callback rule 4) and must not re-enter ANY buck_* export -- not just demesne-scoped ones, because every export's exit-drain is a potential log-sink invocation, and invoking the sink under this lock is the rule-4 deadlock (and the mutex is not reentrant besides). This is the load-bearing constraint for M6 render work: work that calls out happens before the lock or after release.
//
// The lock() expect can only fire if a panic escaped this containment. It then unwinds into guard's outer catch_unwind, so every subsequent demesne call returns Panic with this message (plus per-call stderr backtraces) -- degraded but loud, and it can't deadlock or alias state.
fn with_demesne<R>(
    rid: Rid,
    body: impl FnOnce(&mut Demesne) -> Result<R, FfiError>,
) -> Result<R, FfiError> {
    let mut demesnes = DEMESNES
        .lock()
        .expect("DEMESNES mutex poisoned: a panic escaped the demesne-scoped containment");
    let demesne = match demesnes.get_mut(rid) {
        Some(demesne) => demesne,
        None => {
            let raw = rid.raw();
            return Err(FfiError::new(
                FfiCode::InvalidArgument,
                format!("invalid demesne handle {raw:#x}: stale, destroyed, or never created"),
            ));
        }
    };
    if demesne.poisoned {
        return Err(FfiError::new(
            FfiCode::Poisoned,
            "demesne is poisoned by an earlier panic; its state is suspect -- destroy it",
        ));
    }
    match catch_unwind(AssertUnwindSafe(|| body(demesne))) {
        Ok(result) => result,
        Err(payload) => {
            demesnes
                .get_mut(rid)
                .expect("demesne vanished under the held DEMESNES lock")
                .poisoned = true;
            Err(FfiError::new(FfiCode::Panic, panic_message(&payload)))
        }
    }
}

/// Creates a demesne and returns its handle.
#[buck_export(ret_names(demesne))]
fn demesne_create() -> Result<Rid, FfiError> {
    let demesne = Demesne { poisoned: false };
    let rid = DEMESNES
        .lock()
        .expect("DEMESNES mutex poisoned: a panic escaped the demesne-scoped containment")
        .insert(demesne);
    Ok(rid)
}

/// Destroys a demesne. Deliberately works on poisoned demesnes too -- explicit destroy is the teardown-ordering guarantee.
#[buck_export]
fn demesne_destroy(demesne: Rid) -> Result<(), FfiError> {
    // Deliberately not with_demesne: destroy must work on poisoned demesnes, and remove gives the detailed RidError for the message.
    let mut demesnes = DEMESNES
        .lock()
        .expect("DEMESNES mutex poisoned: a panic escaped the demesne-scoped containment");
    match demesnes.remove(demesne) {
        Ok(_demesne) => Ok(()),
        Err(why) => Err(invalid_handle_error(demesne, why)),
    }
}

/// Permanent test-only export: panics inside the demesne scope, proving the per-instance poison mechanism against the shipped artifact (companions: buck_globals_test_panic for the global latch, buck_test_panic for bare containment).
#[buck_export]
fn demesne_test_panic(demesne: Rid) -> Result<(), FfiError> {
    with_demesne(demesne, |_demesne| {
        panic!("deliberate panic for demesne poison testing")
    })
}

/// Permanent test-only export: logs two records then panics, proving the records-before-result guarantee against the shipped artifact -- the exit-drain must deliver both, in order, before the Panic code returns, and the demesne must be poisoned afterward.
#[buck_export]
fn demesne_test_log_then_panic(demesne: Rid) -> Result<(), FfiError> {
    with_demesne(demesne, |_demesne| {
        log::error!("first record before the panic");
        log::error!("second record before the panic");
        panic!("deliberate panic after logging")
    })
}
