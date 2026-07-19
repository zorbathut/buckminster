//! buckminster-core: kernel primitives under a flat C FFI. M1 exports are the walking skeleton proving the FFI discipline; real engine surface arrives from M4 on.

// The buck_* macros emit paths rooted at ::buckminster_core::ffi so the same expansion works from any exporting crate; this alias makes those paths resolve inside this crate too.
extern crate self as buckminster_core;

mod demesne;
pub mod ffi;
mod globals;
pub mod keycode;
pub mod logging;
pub mod platform;
pub mod rid;

use ffi::{FfiCode, FfiError, buck_export, buck_trait};
use globals::EngineConfig;

// Export conventions (see ARCHITECTURE.md, FFI section): fallible exports return an i32 FfiCode and write results through out-params; out-params are non-null by caller contract. #[buck_export] mechanizes all of this; buck_last_error_message (ffi.rs) is the one deliberate hand-written exception.

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

/// Permanent test-only trait: the callback-machinery demonstrator (M1's buck_callback_invoke, generalized). Its C# test implementations do arithmetic, throw, and reenter the FFI, pinning the full round trip on every target. Internal: a test demonstrator must not appear in game-facing C# surface.
#[buck_trait(internal)]
pub trait CallbackDemo {
    /// Transforms a value however the implementation likes.
    fn invoke(&mut self, value: i32) -> Result<i32, FfiError>;
}

/// Permanent test-only export: proxies one CallbackDemo invocation, taking ownership of the vtable -- the drop at return releases the C#-side registration, so the test also pins the deterministic-lifetime half of the contract.
#[buck_export(ret_names(result))]
fn test_callback_demo(callback: &CallbackDemoVtable, value: i32) -> Result<i32, FfiError> {
    let mut proxy = CallbackDemoProxy::from_vtable(*callback);
    proxy.invoke(value)
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
