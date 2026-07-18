//! buckminster-core: kernel primitives under a flat C FFI. M1 exports are the walking skeleton proving the FFI discipline; real engine surface arrives from M4 on.

// The buck_* macros emit paths rooted at ::buckminster_core::ffi so the same expansion works from any exporting crate; this alias makes those paths resolve inside this crate too.
extern crate self as buckminster_core;

mod engine;
pub mod ffi;
pub mod keycode;
pub mod logging;
pub mod platform;
pub mod rid;

use engine::EngineConfig;
use ffi::{FfiCode, FfiError, buck_export, guard};

/// The callback shape mirrored by C#'s `delegate* unmanaged<ulong, int, int*, int>`: userdata key in, result via out-param, FfiCode-style i32 return (nonzero means the callback failed and contained its own exception).
pub type CallbackFn = extern "C" fn(userdata: u64, value: i32, out_result: *mut i32) -> i32;

// Export conventions (see ARCHITECTURE.md, FFI section): fallible exports return an i32 FfiCode and write results through out-params; out-params are non-null by caller contract, which is why exports with out-params are `unsafe fn` (clippy::not_unsafe_ptr_arg_deref agrees). Every body runs inside ffi::guard. #[buck_export] mechanizes all of this; the remaining hand-written externs are the fn-pointer family, inexpressible until #[buck_trait] lands.

/// Adds two i32s, erroring on overflow -- the M1 walking-skeleton demonstrator.
#[buck_export(ret_names(sum))]
fn add(a: i32, b: i32) -> Result<i32, FfiError> {
    a.checked_add(b).ok_or_else(|| {
        FfiError::new(
            FfiCode::InvalidArgument,
            format!("buck_add overflow: {a} + {b} does not fit in i32"),
        )
    })
}

/// # Safety
/// `out_result` must be non-null and writable (the standard out-param contract).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn buck_callback_invoke(
    callback: CallbackFn,
    userdata: u64,
    value: i32,
    out_result: *mut i32,
) -> i32 {
    guard(|| {
        let mut result = 0;
        let callback_code = callback(userdata, value, &mut result);
        if callback_code != 0 {
            return Err(FfiError::new(
                FfiCode::CallbackError,
                format!("callback returned error code {callback_code}"),
            ));
        }
        unsafe {
            *out_result = result;
        }
        Ok(())
    })
}

/// Permanent test-only export: proves panic containment against the shipped artifact (feature-gating it would mean tests run against a different .so than the build produced).
#[buck_export]
fn test_panic() -> Result<(), FfiError> {
    panic!("deliberate panic for FFI containment testing")
}

/// Permanent test-only export: negates a bool through the generated bool-as-u8 marshaling in the plain (infallible) form -- the emitter's bool paths have no production consumer yet, so this probe keeps them exercised on every target.
#[buck_export]
fn test_bool_negate(flag: bool) -> bool {
    !flag
}

/// Permanent test-only export: echoes an EngineConfig, exercising the generated struct-by-ref parameter and struct-by-value return paths that have no production consumer yet.
#[buck_export]
fn test_config_echo(config: &EngineConfig) -> EngineConfig {
    *config
}
