//! buckminster-ffi-macros: the FFI binding generator's Rust half. #[buck_export] turns an idiomatic Rust fn into the `extern "C" buck_*` export (guard rails, pointer marshaling, out-params) and registers dump metadata; #[buck_struct]/#[buck_enum] mark mirrored types. The C# half is generated from the metadata dump by tools/lib/ffigen.py.
//!
//! The type vocabulary is deliberately narrow (see ARCHITECTURE.md, FFI section); anything outside it is a loud compile error here, never a new marshalling layer.

use proc_macro::TokenStream;

mod export;
mod mirror;
mod shared;

/// On an idiomatic `fn name(...) -> Result<T, FfiError>` or plain `fn name(...) -> T`: generates the `#[no_mangle] extern "C" fn buck_name(...)` wrapper (guard, pointer checks, span/str marshaling, returns -> out-params) plus dump metadata. The Result form is for FFI/lifecycle problems only (stale handles, wrong thread, protocol violations); domain failures are ordinary returned data, and a fn with no FFI-lifecycle concerns declares the plain form. Args: `public` (generated C# wrapper visibility), `ret_names(a, b, ...)` (out-param / tuple-field names; required for tuple returns).
#[proc_macro_attribute]
pub fn buck_export(attr: TokenStream, item: TokenStream) -> TokenStream {
    export::expand(attr.into(), item.into())
        .unwrap_or_else(syn::Error::into_compile_error)
        .into()
}

/// On a `#[repr(C)]` struct: marks it mirrored (BuckMirrored + dump metadata). Fields must be scalars or other mirrored structs. Args: `public` (generated C# mirror visibility).
#[proc_macro_attribute]
pub fn buck_struct(attr: TokenStream, item: TokenStream) -> TokenStream {
    mirror::expand_struct(attr.into(), item.into())
        .unwrap_or_else(syn::Error::into_compile_error)
        .into()
}

/// On a `#[repr(i32)]`/`#[repr(u32)]` unit enum with explicit discriminants: marks it mirrored (BuckMirrored + dump metadata). Args: `public` (generated C# mirror visibility).
#[proc_macro_attribute]
pub fn buck_enum(attr: TokenStream, item: TokenStream) -> TokenStream {
    mirror::expand_enum(attr.into(), item.into())
        .unwrap_or_else(syn::Error::into_compile_error)
        .into()
}
