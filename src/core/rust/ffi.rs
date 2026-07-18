//! The FFI entry-point rails: every fallible `buck_*` export is a thin `extern "C"` fn whose body runs inside [`guard`], which contains panics, maps errors to codes, and stores the message for [`buck_last_error_message`]. Public because macro-generated export wrappers (buckminster-ffi-macros) call these through the stable `::buckminster_core::ffi::` path -- from this crate and from any future module crate that depends on core alike.

use std::cell::RefCell;
use std::ffi::{CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};

// Re-exported so exporting code imports the attribute macros and the rails from one place (`use crate::ffi::{buck_export, guard, ...}`).
pub use buckminster_ffi_macros::{buck_enum, buck_export, buck_struct};

#[cfg(feature = "ffi-dump")]
pub use inventory;
#[cfg(feature = "ffi-dump")]
pub mod meta;

/// Marker implemented by every #[buck_struct]/#[buck_enum] type. #[buck_export] asserts it for each mirrored type in a signature, so an unmarked type fails EVERY build with a trait error, not just dump builds.
pub trait BuckMirrored {}

/// Conversion contract implemented by every #[buck_enum] type: the repr scalar it crosses the FFI as, with validated construction. The export macro's enum-param glue goes through this, so an out-of-range discriminant arriving from C# is InvalidArgument -- never a transmuted invalid enum value.
pub trait BuckEnum: Copy {
    type Repr: Copy + std::fmt::Display;
    fn buck_from_raw(raw: Self::Repr) -> Option<Self>;
    fn buck_raw(self) -> Self::Repr;
}

/// Error codes returned by every fallible buck_* export. Future codes append; no generic catch-all.
#[buck_enum]
#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum FfiCode {
    Ok = 0,
    Panic = 1,
    CallbackError = 2,
    InvalidArgument = 3,
    EnginePoisoned = 4,
}

/// The FFI-problem channel: stale/destroyed handles, poisoned engines, wrong-thread calls, protocol violations (bad pointers, invalid UTF-8), unconfigured subsystems. Surfaced to C# as exceptions because hitting one means a bug or a dead object -- never expected control flow. Domain failures (an operation that can legitimately not succeed) are NOT FfiErrors: model them as ordinary returned data, and give the export the plain non-Result form when it has no FFI-lifecycle concerns at all.
pub struct FfiError {
    pub code: FfiCode,
    pub message: String,
}

impl FfiError {
    pub fn new(code: FfiCode, message: impl Into<String>) -> FfiError {
        FfiError {
            code,
            message: message.into(),
        }
    }
}

thread_local! {
    // The message backing buck_last_error_message, per calling thread. None whenever the last guarded call on this thread succeeded.
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

/// Runs an export body with the standard rails: `catch_unwind` so no panic crosses the FFI, the exit-drain (buffered log records deliver at the tail of EVERY export, records-before-result -- see logging::drain_at_exit), error-to-code mapping, and last-error storage. Success clears the stored error, so a null from `buck_last_error_message` always means "last call on this thread succeeded".
///
/// Sequencing is load-bearing: body, then drain, then LAST_ERROR as the FINAL act. The log sink is C# code whose nested `buck_*` calls clobber this thread's LAST_ERROR -- storing the body's error before draining would let a sink's successful nested call clear it, breaking "null means success" exactly on the failed-call-with-diagnostics case.
///
/// LAST_ERROR is only ever borrowed inside store_error/clear_error, never across `body()` or the drain -- a callback reentering `buck_*` on this thread nests `guard` (PLAN.md callback rule 4, applied to the wrapper itself).
pub fn guard(body: impl FnOnce() -> Result<(), FfiError>) -> i32 {
    // The default panic hook stays installed: a contained panic still prints its backtrace to stderr, which is loud and intended.
    //
    // AssertUnwindSafe is justified because the only state this module observes after a caught panic is LAST_ERROR, which is written after everything else. The rail's contract for callers: Panic means the state the body was mutating is suspect. For engine-scoped exports that suspicion is enforced -- engine.rs catches the panic inside the ENGINES lock scope, marks the engine poisoned (every later op but destroy returns EnginePoisoned), and re-reports it through this rail as an FfiError; a panic reaching the catch_unwind below is one from outside any engine scope.
    let body_outcome = catch_unwind(AssertUnwindSafe(body));
    // The drain runs unconditionally (a routine error return or a contained panic deserves its diagnostics more, not less) and under its own catch_unwind: it executes outside the body's containment, and an unwind from here would cross the extern "C" boundary and abort the process.
    let drain_outcome = catch_unwind(AssertUnwindSafe(crate::logging::drain_at_exit));
    // Precedence: the body's outcome always wins the return code. A drain failure surfaces as this call's result only when the body succeeded; when both fail, the body's code and message return and the sink's exception is still in the C# stash (FfiCall takes it unconditionally and attaches it -- true for the standard thunk, which always stashes before returning nonzero; a hypothetical non-stashing registrant would lose its drain error here), while a drain PANIC alongside a body failure is reported only by the panic hook's stderr backtrace -- accepted, the body's error must not be masked.
    match body_outcome {
        Ok(Ok(())) => match drain_outcome {
            Ok(Ok(())) => {
                clear_error();
                FfiCode::Ok as i32
            }
            Ok(Err(error)) => {
                store_error(error.message);
                error.code as i32
            }
            Err(payload) => {
                store_error(panic_message(&payload));
                FfiCode::Panic as i32
            }
        },
        Ok(Err(error)) => {
            // An FfiError carrying Ok would report success to the caller while storing an error message, silently breaking "null means the last call succeeded".
            debug_assert!(
                error.code != FfiCode::Ok,
                "FfiError constructed with FfiCode::Ok"
            );
            store_error(error.message);
            error.code as i32
        }
        Err(payload) => {
            store_error(panic_message(&payload));
            FfiCode::Panic as i32
        }
    }
}

// CString::new fails on interior NULs; the message must never be silently lost, so sanitize instead.
fn store_error(message: String) {
    let sanitized = message.replace('\0', "\\0");
    let cstring = CString::new(sanitized).expect("no interior NULs remain after sanitizing");
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = Some(cstring);
    });
}

fn clear_error() {
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = None;
    });
}

/// The human-readable text of a caught panic payload. Shared by `guard` and the engine-scoped inner catch (engine.rs), which converts a panic into a poison + Panic-coded FfiError instead of letting it unwind through the ENGINES MutexGuard.
pub fn panic_message(payload: &Box<dyn std::any::Any + Send>) -> String {
    if let Some(text) = payload.downcast_ref::<&str>() {
        (*text).to_string()
    } else if let Some(text) = payload.downcast_ref::<String>() {
        text.clone()
    } else {
        "panic payload of unknown type".to_string()
    }
}

/// The (ptr, len) span contract for string-taking exports, with the empty-span null pointer allowance (C#'s fixed on an empty array pins null; from_raw_parts(null, 0) is UB-adjacent and must never be constructed). Shared by hand-written exports and macro-generated wrappers alike.
///
/// # Safety
/// `ptr` must point to `len` readable bytes when `len > 0` (invalid UTF-8 is rejected as an error, not UB).
pub unsafe fn utf8_arg<'a>(ptr: *const u8, len: usize, what: &str) -> Result<&'a str, FfiError> {
    if len == 0 {
        return Ok("");
    }
    if ptr.is_null() {
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            format!("{what}: null pointer with nonzero length"),
        ));
    }
    let bytes = unsafe { std::slice::from_raw_parts(ptr, len) };
    std::str::from_utf8(bytes)
        .map_err(|_| FfiError::new(FfiCode::InvalidArgument, format!("{what}: not valid UTF-8")))
}

/// Null if the last `buck_*` call on this thread succeeded; otherwise the error message, valid until the next `buck_*` call other than `buck_last_error_message` itself on the same thread (reading is non-destructive and repeatable). Callers copy immediately. This is the one deliberate exception to "every export runs inside guard": it is infallible, and routing it through guard would clear the very error it exists to read.
#[unsafe(no_mangle)]
pub extern "C" fn buck_last_error_message() -> *const c_char {
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some(message) => message.as_ptr(),
        None => std::ptr::null(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn stored_error() -> Option<String> {
        LAST_ERROR.with(|slot| {
            slot.borrow().as_ref().map(|message| {
                message
                    .to_str()
                    .expect("stored messages are valid UTF-8")
                    .to_string()
            })
        })
    }

    #[test]
    fn guard_contains_panic_and_captures_message() {
        let code = guard(|| panic!("test panic message"));
        assert_eq!(code, FfiCode::Panic as i32);
        assert_eq!(stored_error().as_deref(), Some("test panic message"));
    }

    #[test]
    fn guard_maps_error_to_its_code() {
        let code = guard(|| Err(FfiError::new(FfiCode::InvalidArgument, "bad input")));
        assert_eq!(code, FfiCode::InvalidArgument as i32);
        assert_eq!(stored_error().as_deref(), Some("bad input"));
    }

    #[test]
    fn guard_success_clears_stored_error() {
        guard(|| Err(FfiError::new(FfiCode::InvalidArgument, "bad input")));
        let code = guard(|| Ok(()));
        assert_eq!(code, FfiCode::Ok as i32);
        assert_eq!(stored_error(), None);
    }

    #[test]
    fn store_error_sanitizes_interior_nuls() {
        store_error("before\0after".to_string());
        assert_eq!(stored_error().as_deref(), Some("before\\0after"));
    }
}
