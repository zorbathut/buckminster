namespace Buckminster.Ffi;

// Error codes returned by every fallible buck_* export. Hand-mirrored from the Rust side -- keep in sync with FfiCode in src/core/rust/ffi.rs.
internal enum FfiCode
{
    Ok = 0,
    Panic = 1,
    CallbackError = 2,
    InvalidArgument = 3,
}
