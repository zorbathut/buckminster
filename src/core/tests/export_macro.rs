//! Integration tests for the #[buck_export]/#[buck_struct] expansion: each probe fn covers one shape of the type vocabulary, and the tests call the GENERATED buck_* externs the way C# will -- raw pointers, spans, codes -- proving the marshaling rails (null checks, UTF-8 validation, out-param writes, bool-as-u8) against real generated code. The pilot conversion in core covers scalars/RIDs/struct-ref in production; these cover the rest of the vocabulary so chunk 3's conversions land on proven rails.

use buckminster_core::ffi::{FfiCode, FfiError, buck_enum, buck_export, buck_struct};
use buckminster_core::rid::Rid;

#[buck_struct]
#[repr(C)]
#[derive(Clone, Copy)]
struct ProbePair {
    a: u32,
    b: u32,
}

/// Byte length of a UTF-8 string argument.
#[buck_export]
fn probe_str_len(text: &str) -> Result<u64, FfiError> {
    Ok(text.len() as u64)
}

/// Sum of a scalar slice-in.
#[buck_export]
fn probe_slice_sum(values: &[u32]) -> Result<u64, FfiError> {
    Ok(values.iter().map(|value| u64::from(*value)).sum())
}

/// Fills a slice-out buffer with 1-based indices; returns the poll shape (written, remaining).
#[buck_export(ret_names(written, remaining))]
fn probe_fill(buf: &mut [u32], available: u32) -> Result<(u32, u32), FfiError> {
    let written = (buf.len() as u32).min(available);
    for (index, slot) in buf.iter_mut().take(written as usize).enumerate() {
        *slot = index as u32 + 1;
    }
    Ok((written, available - written))
}

/// Doubles a value in place (&mut scalar in-out).
#[buck_export]
fn probe_double(value: &mut u64) -> Result<(), FfiError> {
    *value *= 2;
    Ok(())
}

/// Negates a bool (bool param and bool return, both crossing as u8).
#[buck_export]
fn probe_toggle(flag: bool) -> Result<bool, FfiError> {
    Ok(!flag)
}

/// Sum of a struct passed by reference.
#[buck_export]
fn probe_pair_sum(pair: &ProbePair) -> Result<u64, FfiError> {
    Ok(u64::from(pair.a) + u64::from(pair.b))
}

/// Constructs a struct return value.
#[buck_export]
fn probe_pair_make(a: u32, b: u32) -> Result<ProbePair, FfiError> {
    Ok(ProbePair { a, b })
}

fn last_error() -> String {
    let ptr = buckminster_core::ffi::buck_last_error_message();
    assert!(!ptr.is_null(), "expected a stored error message");
    unsafe { std::ffi::CStr::from_ptr(ptr) }
        .to_str()
        .expect("error messages are UTF-8")
        .to_string()
}

#[test]
fn str_in_valid_and_empty_and_null() {
    let text = "hello";
    let mut len = 0u64;
    let code = unsafe { buck_probe_str_len(text.as_ptr(), text.len(), &mut len) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(len, 5);

    // Empty span with a null pointer is the C# fixed-on-empty-array case and must succeed.
    let code = unsafe { buck_probe_str_len(std::ptr::null(), 0, &mut len) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(len, 0);

    let code = unsafe { buck_probe_str_len(std::ptr::null(), 3, &mut len) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
    assert!(
        last_error().contains("null pointer with nonzero length"),
        "unexpected message: {}",
        last_error()
    );
}

#[test]
fn str_in_rejects_invalid_utf8() {
    let bytes = [0xffu8, 0xfe];
    let mut len = 0u64;
    let code = unsafe { buck_probe_str_len(bytes.as_ptr(), bytes.len(), &mut len) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
    assert!(
        last_error().contains("not valid UTF-8"),
        "unexpected message: {}",
        last_error()
    );
}

#[test]
fn slice_in_sums_and_checks_null() {
    let values = [1u32, 2, 3, 4];
    let mut sum = 0u64;
    let code = unsafe { buck_probe_slice_sum(values.as_ptr(), values.len(), &mut sum) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(sum, 10);

    let code = unsafe { buck_probe_slice_sum(std::ptr::null(), 0, &mut sum) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(sum, 0);

    let code = unsafe { buck_probe_slice_sum(std::ptr::null(), 2, &mut sum) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
}

#[test]
fn slice_out_fills_and_returns_the_poll_tuple() {
    let mut buf = [0u32; 3];
    let mut written = 0u32;
    let mut remaining = 0u32;
    let code =
        unsafe { buck_probe_fill(buf.as_mut_ptr(), buf.len(), 5, &mut written, &mut remaining) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(written, 3);
    assert_eq!(remaining, 2);
    assert_eq!(buf, [1, 2, 3]);

    // Zero-capacity with a null buffer is legal (the size-probe call pattern).
    let code = unsafe { buck_probe_fill(std::ptr::null_mut(), 0, 5, &mut written, &mut remaining) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(written, 0);
    assert_eq!(remaining, 5);
}

#[test]
fn ref_in_out_doubles_and_checks_null() {
    let mut value = 21u64;
    let code = unsafe { buck_probe_double(&mut value) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(value, 42);

    let code = unsafe { buck_probe_double(std::ptr::null_mut()) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
    assert!(
        last_error().contains("value is null"),
        "unexpected message: {}",
        last_error()
    );
}

#[test]
fn bool_crosses_as_u8_both_directions() {
    let mut out = 7u8;
    let code = unsafe { buck_probe_toggle(0, &mut out) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(out, 1);

    // Any nonzero input reads as true (C convention), and false writes exactly 0.
    let code = unsafe { buck_probe_toggle(3, &mut out) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(out, 0);
}

#[test]
fn struct_ref_in_and_struct_return() {
    let pair = ProbePair { a: 40, b: 2 };
    let mut sum = 0u64;
    let code = unsafe { buck_probe_pair_sum(&pair, &mut sum) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(sum, 42);

    let code = unsafe { buck_probe_pair_sum(std::ptr::null(), &mut sum) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
    assert!(
        last_error().contains("pair is null"),
        "unexpected message: {}",
        last_error()
    );

    let mut made = ProbePair { a: 0, b: 0 };
    let code = unsafe { buck_probe_pair_make(6, 7, &mut made) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!((made.a, made.b), (6, 7));
}

/// Fills a mirror-element slice-out buffer (the PlatformEventRaw poll shape in miniature).
#[buck_export(ret_names(written))]
fn probe_pairs_fill(buf: &mut [ProbePair]) -> Result<u32, FfiError> {
    for (index, slot) in buf.iter_mut().enumerate() {
        *slot = ProbePair {
            a: index as u32,
            b: index as u32 * 10,
        };
    }
    Ok(buf.len() as u32)
}

/// Swaps a struct's fields in place (&mut Struct in-out).
#[buck_export]
fn probe_pair_swap(pair: &mut ProbePair) -> Result<(), FfiError> {
    std::mem::swap(&mut pair.a, &mut pair.b);
    Ok(())
}

#[test]
fn mirror_slice_out_fills_structs() {
    let mut buf = [ProbePair { a: 99, b: 99 }; 2];
    let mut written = 0u32;
    let code = unsafe { buck_probe_pairs_fill(buf.as_mut_ptr(), buf.len(), &mut written) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(written, 2);
    assert_eq!((buf[1].a, buf[1].b), (1, 10));
}

#[test]
fn mirror_ref_in_out_mutates_in_place() {
    let mut pair = ProbePair { a: 1, b: 2 };
    let code = unsafe { buck_probe_pair_swap(&mut pair) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!((pair.a, pair.b), (2, 1));

    let code = unsafe { buck_probe_pair_swap(std::ptr::null_mut()) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
}

/// Round-trips a Rid handle (typed Rid in Rust, plain u64 across the boundary; the macro hides the glue).
#[buck_export]
fn probe_rid_roundtrip(rid: Rid) -> Result<Rid, FfiError> {
    Ok(rid)
}

#[test]
fn rid_round_trips_as_plain_u64() {
    let mut out = 0u64;
    let code = unsafe { buck_probe_rid_roundtrip(0x1234_5678_9abc_def0, &mut out) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(out, 0x1234_5678_9abc_def0);
}

/// Doubles a value with the plain (infallible) form -- no Result, no FfiError channel; the rails still guard it (Panic and marshaling errors remain possible at the raw layer).
#[buck_export]
fn probe_plain_double(value: u32) -> u64 {
    u64::from(value) * 2
}

#[test]
fn plain_form_returns_through_out_param() {
    let mut out = 0u64;
    let code = unsafe { buck_probe_plain_double(21, &mut out) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(out, 42);
}

#[buck_enum]
#[repr(u32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ProbeMode {
    Alpha = 1,
    Beta = 2,
}

/// Echoes an enum's discriminant (enum-by-value param: crosses as its repr, validated in the generated glue).
#[buck_export]
fn probe_mode_value(mode: ProbeMode) -> u32 {
    mode as u32
}

// i32 repr with a negative discriminant: covers the signed literal branch of the generated BuckEnum match (patterns like -1i32) and i32-side runtime validation.
#[buck_enum]
#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ProbeSigned {
    Minus = -1,
    Plus = 1,
}

/// Echoes a signed enum's discriminant.
#[buck_export]
fn probe_signed_value(mode: ProbeSigned) -> i32 {
    mode as i32
}

#[test]
fn signed_enum_param_validates_discriminants() {
    let mut out = 0i32;
    let code = unsafe { buck_probe_signed_value(-1, &mut out) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(out, -1);

    let code = unsafe { buck_probe_signed_value(0, &mut out) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
    assert!(
        last_error().contains("invalid ProbeSigned discriminant 0"),
        "unexpected message: {}",
        last_error()
    );
}

#[test]
fn enum_param_validates_discriminants() {
    let mut out = 0u32;
    let code = unsafe { buck_probe_mode_value(2, &mut out) };
    assert_eq!(code, FfiCode::Ok as i32);
    assert_eq!(out, 2);

    let code = unsafe { buck_probe_mode_value(7, &mut out) };
    assert_eq!(code, FfiCode::InvalidArgument as i32);
    assert!(
        last_error().contains("invalid ProbeMode discriminant 7"),
        "unexpected message: {}",
        last_error()
    );
}
