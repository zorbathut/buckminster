//! buckminster-host-desktop: the desktop executor's Rust half -- the native link root C# loads on desktop (host-as-executor composition: core is a library linked into this cdylib, not the other way around). The winit platform layer lands here in the hoist chunk; until then this crate is the multi-crate FFI pipeline's proof.

// Core must be a hard reference of this crate: an unreferenced dependency is not linked into the cdylib at all, which would silently drop every buck_* export core defines. The macro-emitted ::buckminster_core:: paths below reference it too; this line is the explicit guarantee that survives refactors (underscore import: linkage only, no re-exported API).
use buckminster_core as _;

use buckminster_core::ffi::buck_export;

/// Permanent multi-crate canary: proves a non-core crate's #[buck_export] survives the whole macro -> dump -> ffigen -> pinvoke pipeline. The desktop host calls it once at startup and fails loudly on a wrong answer.
#[buck_export]
fn test_host_probe(x: i32) -> i32 {
    x.wrapping_mul(2).wrapping_add(1)
}

#[cfg(test)]
mod tests {
    use buckminster_core::ffi::FfiCode;

    fn probe(x: i32) -> i32 {
        let mut out = 0i32;
        let code = unsafe { super::buck_test_host_probe(x, &mut out) };
        assert_eq!(code, FfiCode::Ok as i32);
        out
    }

    #[test]
    fn probe_round_trips_through_the_generated_extern() {
        assert_eq!(probe(20), 41);
        assert_eq!(probe(-1), -1);
        assert_eq!(probe(i32::MAX), i32::MAX.wrapping_mul(2).wrapping_add(1));
    }
}
