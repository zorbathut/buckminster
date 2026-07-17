//! Dump-only FFI metadata: #[buck_export]/#[buck_struct]/#[buck_enum] register these via inventory, and buckminster-ffi-dump serializes them for the C# generator. Never compiled into shipped artifacts -- this module exists only under the ffi-dump feature, which only the dump bin enables.
//!
//! The raw extern "C" signature is recorded here too (raw_params), authored by the macro at expansion time: the C# emitter consumes it verbatim instead of re-deriving it, so the expansion rules live in exactly one place and a rules mismatch between the Rust extern and the C# import cannot happen.

use serde::Serialize;

/// The scalar vocabulary. Bool never appears in a raw signature (it crosses as U8); it exists here so idiomatic params/returns can declare it and both wrappers translate.
#[derive(Serialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(rename_all = "snake_case")]
pub enum MetaScalar {
    I8,
    U8,
    I16,
    U16,
    I32,
    U32,
    I64,
    U64,
    F32,
    F64,
    // snake_case would render this "u_size"; the JSON contract says "usize" like the Rust type it names.
    #[serde(rename = "usize")]
    USize,
    Bool,
}

/// A semantic (idiomatic-layer) type: what the Rust fn declares and what the C# wrapper exposes.
#[derive(Serialize, Clone, Copy, Debug)]
#[serde(rename_all = "snake_case", tag = "kind")]
pub enum MetaType {
    Scalar {
        scalar: MetaScalar,
    },
    /// `&str` in; crosses as (ptr, len).
    Str,
    /// `&[T]` in; crosses as (ptr, len).
    SliceIn {
        elem: &'static MetaType,
    },
    /// `&mut [T]` out-buffer (the poll shape); crosses as (ptr, cap).
    SliceOut {
        elem: &'static MetaType,
    },
    /// `&mut T` in-out; C# `ref`.
    RefInOut {
        inner: &'static MetaType,
    },
    /// A mirrored type, identified through the BuckMirror trait (no token stringification). #[buck_enum] types implement BuckMirror too, so the emitter resolves the name against BOTH the structs and enums sections and fails loudly on a miss -- the kind is deliberately not duplicated here.
    Mirror {
        crate_name: &'static str,
        name: &'static str,
    },
    /// A registry handle: typed `Rid` in Rust, plain u64 at the raw layer, `ulong` in C# today -- the distinct kind is what lets the emitter grow typed C# handles later without touching Rust.
    Rid,
}

/// A raw (extern "C" layer) parameter type: what the generated extern declares and what the C# [LibraryImport] declares.
#[derive(Serialize, Clone, Copy, Debug)]
#[serde(rename_all = "snake_case", tag = "kind")]
pub enum MetaRawType {
    Scalar {
        scalar: MetaScalar,
    },
    ConstPtrScalar {
        scalar: MetaScalar,
    },
    MutPtrScalar {
        scalar: MetaScalar,
    },
    ConstPtrMirror {
        crate_name: &'static str,
        name: &'static str,
    },
    MutPtrMirror {
        crate_name: &'static str,
        name: &'static str,
    },
}

#[derive(Serialize, Debug)]
pub struct MetaParam {
    pub name: &'static str,
    pub ty: MetaType,
}

/// One value produced by an export: none = unit, one = return value, several = named tuple (names from #[buck_export(ret_names(...))]).
#[derive(Serialize, Debug)]
pub struct MetaReturn {
    pub name: &'static str,
    pub ty: MetaType,
}

#[derive(Serialize, Debug)]
pub struct MetaRawParam {
    pub name: &'static str,
    pub ty: MetaRawType,
}

#[derive(Serialize, Debug)]
pub struct MetaExport {
    pub crate_name: &'static str,
    pub name: &'static str,
    pub symbol: &'static str,
    pub docs: &'static [&'static str],
    pub public: bool,
    pub params: &'static [MetaParam],
    pub returns: &'static [MetaReturn],
    pub raw_params: &'static [MetaRawParam],
}

#[derive(Serialize, Debug)]
pub struct MetaField {
    pub name: &'static str,
    pub ty: MetaType,
    pub docs: &'static [&'static str],
}

#[derive(Serialize, Debug)]
pub struct MetaStruct {
    pub crate_name: &'static str,
    pub name: &'static str,
    pub docs: &'static [&'static str],
    pub public: bool,
    pub fields: &'static [MetaField],
}

#[derive(Serialize, Debug)]
pub struct MetaVariant {
    pub name: &'static str,
    pub value: i64,
    pub docs: &'static [&'static str],
}

#[derive(Serialize, Debug)]
pub struct MetaEnum {
    pub crate_name: &'static str,
    pub name: &'static str,
    pub docs: &'static [&'static str],
    pub public: bool,
    pub repr: MetaScalar,
    pub variants: &'static [MetaVariant],
}

/// Type identity for mirrored types in export signatures: #[buck_struct]/#[buck_enum] implement this (dump builds only), and #[buck_export] metadata references the consts instead of stringifying tokens.
pub trait BuckMirror {
    const CRATE: &'static str;
    const NAME: &'static str;
}

inventory::collect!(MetaExport);
inventory::collect!(MetaStruct);
inventory::collect!(MetaEnum);
