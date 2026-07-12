//! An integration test deliberately: it runs as its own process, which is the whole point -- the logging CONFIGURED flag is process-sticky, so "not configured yet" is assertable only in a process where no engine was ever created. In-crate unit tests and the C# suites can't test this (their processes configure logging early and forever).

#[test]
fn log_before_configure_is_a_loud_error() {
    let message = b"too early";
    let code = unsafe { buckminster_core::logging::buck_log(3, message.as_ptr(), message.len()) };
    // FfiCode::InvalidArgument = 3 (the enum lives in the crate-private ffi module; the raw code is the FFI contract anyway).
    assert_eq!(code, 3);
}
