//! Pieces shared by the three attribute macros: the scalar vocabulary, type classification, attribute-arg parsing, and doc extraction.

use proc_macro2::TokenStream;
use quote::{format_ident, quote};
use syn::punctuated::Punctuated;
use syn::{Attribute, Expr, Ident, Lit, Meta, Token, Type, UnOp};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Scalar {
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
    USize,
    Bool,
}

impl Scalar {
    pub fn from_type(ty: &Type) -> Option<Scalar> {
        let Type::Path(path) = ty else {
            return None;
        };
        let ident = path.path.get_ident()?;
        let scalar = match ident.to_string().as_str() {
            "i8" => Scalar::I8,
            "u8" => Scalar::U8,
            "i16" => Scalar::I16,
            "u16" => Scalar::U16,
            "i32" => Scalar::I32,
            "u32" => Scalar::U32,
            "i64" => Scalar::I64,
            "u64" => Scalar::U64,
            "f32" => Scalar::F32,
            "f64" => Scalar::F64,
            "usize" => Scalar::USize,
            "bool" => Scalar::Bool,
            _ => return None,
        };
        Some(scalar)
    }

    /// The Rust type this scalar occupies at the raw extern "C" layer (bool crosses as u8: 1-byte C ABI on the Rust side, and it sidesteps .NET's 4-byte BOOL marshaling default).
    pub fn raw_tokens(self) -> TokenStream {
        match self {
            Scalar::I8 => quote!(i8),
            Scalar::U8 | Scalar::Bool => quote!(u8),
            Scalar::I16 => quote!(i16),
            Scalar::U16 => quote!(u16),
            Scalar::I32 => quote!(i32),
            Scalar::U32 => quote!(u32),
            Scalar::I64 => quote!(i64),
            Scalar::U64 => quote!(u64),
            Scalar::F32 => quote!(f32),
            Scalar::F64 => quote!(f64),
            Scalar::USize => quote!(usize),
        }
    }

    /// The `MetaScalar::X` expression for dump metadata.
    pub fn meta_tokens(self) -> TokenStream {
        let variant = match self {
            Scalar::I8 => "I8",
            Scalar::U8 => "U8",
            Scalar::I16 => "I16",
            Scalar::U16 => "U16",
            Scalar::I32 => "I32",
            Scalar::U32 => "U32",
            Scalar::I64 => "I64",
            Scalar::U64 => "U64",
            Scalar::F32 => "F32",
            Scalar::F64 => "F64",
            Scalar::USize => "USize",
            Scalar::Bool => "Bool",
        };
        let variant = format_ident!("{}", variant);
        quote!(::buckminster_core::ffi::meta::MetaScalar::#variant)
    }

    /// The `MetaScalar::X` expression for RAW-layer dump metadata: like meta_tokens except Bool records as U8, matching raw_tokens -- the raw metadata must describe the extern signature that actually exists, and bool never appears in one.
    pub fn raw_meta_tokens(self) -> TokenStream {
        if self == Scalar::Bool {
            Scalar::U8.meta_tokens()
        } else {
            self.meta_tokens()
        }
    }
}

/// A slice element or &mut inner: scalar or mirrored struct.
#[derive(Clone)]
pub enum Elem {
    Scalar(Scalar),
    Mirror(syn::Path),
}

/// True for the bare `Rid` handle type, recognized by token like the scalars. Misclassification of an unrelated type named Rid is loud, not silent: the emitted glue names `::buckminster_core::rid::Rid` explicitly, so the body's type mismatch fails the build.
pub fn is_rid(ty: &Type) -> bool {
    if let Type::Path(path) = ty {
        return path.path.is_ident("Rid");
    }
    false
}

impl Elem {
    fn classify(ty: &Type) -> syn::Result<Elem> {
        if let Some(scalar) = Scalar::from_type(ty) {
            return Ok(Elem::Scalar(scalar));
        }
        if is_rid(ty) {
            return Err(syn::Error::new_spanned(
                ty,
                "Rid slices and references are outside the FFI vocabulary; pass Rid by value (or use u64 for bulk handle spans)",
            ));
        }
        if let Type::Path(path) = ty {
            return Ok(Elem::Mirror(path.path.clone()));
        }
        Err(syn::Error::new_spanned(
            ty,
            "unsupported element type: expected a scalar or a #[buck_struct]-mirrored type",
        ))
    }

    pub fn rust_tokens(&self) -> TokenStream {
        match self {
            Elem::Scalar(Scalar::Bool) => unreachable!(
                "bool elements are rejected at classification (no element-wise conversion layer)"
            ),
            Elem::Scalar(scalar) => scalar.raw_tokens(),
            Elem::Mirror(path) => quote!(#path),
        }
    }

    pub fn meta_tokens(&self) -> TokenStream {
        match self {
            Elem::Scalar(scalar) => {
                let scalar = scalar.meta_tokens();
                quote!(::buckminster_core::ffi::meta::MetaType::Scalar { scalar: #scalar })
            }
            Elem::Mirror(path) => quote!(::buckminster_core::ffi::meta::MetaType::Mirror {
                crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE,
                name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME,
            }),
        }
    }

    pub fn mirror_path(&self) -> Option<&syn::Path> {
        match self {
            Elem::Scalar(_) => None,
            Elem::Mirror(path) => Some(path),
        }
    }
}

/// The parameter vocabulary of #[buck_export] fns.
pub enum ParamKind {
    Scalar(Scalar),
    Rid,
    Str,
    SliceIn(Elem),
    SliceOut(Elem),
    MirrorRef(syn::Path),
    RefInOut(Elem),
}

pub fn classify_param(ty: &Type) -> syn::Result<ParamKind> {
    if let Some(scalar) = Scalar::from_type(ty) {
        // Bool slices/refs are rejected above via Elem; a bare bool param is fine (crosses as u8).
        return Ok(ParamKind::Scalar(scalar));
    }
    if is_rid(ty) {
        return Ok(ParamKind::Rid);
    }
    match ty {
        Type::Path(_) => Err(syn::Error::new_spanned(
            ty,
            "unsupported parameter type: mirrored structs pass by reference (&T); bare path types must be scalars or Rid",
        )),
        Type::Reference(reference) => {
            let mutable = reference.mutability.is_some();
            match &*reference.elem {
                Type::Slice(slice) => {
                    let elem = Elem::classify(&slice.elem)?;
                    if let Elem::Scalar(Scalar::Bool) = elem {
                        return Err(syn::Error::new_spanned(
                            ty,
                            "bool slices are outside the FFI vocabulary (no element-wise conversion layer); use u8",
                        ));
                    }
                    if mutable {
                        Ok(ParamKind::SliceOut(elem))
                    } else {
                        Ok(ParamKind::SliceIn(elem))
                    }
                }
                Type::Path(path) if path.path.is_ident("str") => {
                    if mutable {
                        return Err(syn::Error::new_spanned(
                            ty,
                            "&mut str is outside the FFI vocabulary; string output uses the &mut [u8] byte-buffer idiom",
                        ));
                    }
                    Ok(ParamKind::Str)
                }
                Type::Path(path) => {
                    if let Some(scalar) = Scalar::from_type(&reference.elem) {
                        if !mutable {
                            return Err(syn::Error::new_spanned(
                                ty,
                                "pass scalars by value, not by shared reference",
                            ));
                        }
                        if scalar == Scalar::Bool {
                            return Err(syn::Error::new_spanned(
                                ty,
                                "&mut bool is outside the FFI vocabulary (bool has no in-place raw representation); use &mut u8 or a return value",
                            ));
                        }
                        return Ok(ParamKind::RefInOut(Elem::Scalar(scalar)));
                    }
                    if is_rid(&reference.elem) {
                        return Err(syn::Error::new_spanned(
                            ty,
                            "pass Rid by value, not by reference (it is a plain u64 handle)",
                        ));
                    }
                    if mutable {
                        Ok(ParamKind::RefInOut(Elem::Mirror(path.path.clone())))
                    } else {
                        Ok(ParamKind::MirrorRef(path.path.clone()))
                    }
                }
                _ => Err(syn::Error::new_spanned(
                    ty,
                    "unsupported reference parameter type: the FFI vocabulary is &str, &[T], &mut [T], &Struct, and &mut T",
                )),
            }
        }
        _ => Err(syn::Error::new_spanned(
            ty,
            "unsupported parameter type: the FFI vocabulary is scalars, &str, &[T], &mut [T], &Struct, and &mut T",
        )),
    }
}

/// One value an export produces (a Result<T> payload, or one element of a tuple payload).
pub enum RetItem {
    Scalar(Scalar),
    Rid,
    Mirror(syn::Path),
}

impl RetItem {
    fn classify(ty: &Type) -> syn::Result<RetItem> {
        if let Some(scalar) = Scalar::from_type(ty) {
            return Ok(RetItem::Scalar(scalar));
        }
        if is_rid(ty) {
            return Ok(RetItem::Rid);
        }
        if let Type::Path(path) = ty {
            return Ok(RetItem::Mirror(path.path.clone()));
        }
        Err(syn::Error::new_spanned(
            ty,
            "unsupported return type: Result payloads are (), scalars, Rid, mirrored structs, or tuples of those",
        ))
    }
}

/// The classified return side of an export: what values come back, and whether the body has the FfiError channel. Fallible (`Result<T, FfiError>`) is for FFI/lifecycle problems only -- stale handles, poisoned engines, wrong thread, protocol violations; a fn whose failures are domain outcomes models them as ordinary data in T and declares the plain form.
pub struct Returns {
    pub items: Vec<RetItem>,
    pub fallible: bool,
}

/// Splits the return type into 0 (unit), 1, or N (tuple) returned values, unwrapping `Result<T, FfiError>` when present.
pub fn classify_returns(output: &syn::ReturnType) -> syn::Result<Returns> {
    let syn::ReturnType::Type(_, ty) = output else {
        return Ok(Returns {
            items: Vec::new(),
            fallible: false,
        });
    };
    let (payload, fallible) = match result_payload(ty) {
        Some(payload) => (payload, true),
        None => (&**ty, false),
    };
    let items = match payload {
        Type::Tuple(tuple) if tuple.elems.is_empty() => Vec::new(),
        Type::Tuple(tuple) => tuple
            .elems
            .iter()
            .map(RetItem::classify)
            .collect::<syn::Result<Vec<RetItem>>>()?,
        other => vec![RetItem::classify(other)?],
    };
    Ok(Returns { items, fallible })
}

/// Some(T) when the type is `Result<T, ...>`, None for the plain (infallible) form.
fn result_payload(ty: &Type) -> Option<&Type> {
    let Type::Path(path) = ty else {
        return None;
    };
    let last = path
        .path
        .segments
        .last()
        .expect("a type path has at least one segment");
    if last.ident != "Result" {
        return None;
    }
    let syn::PathArguments::AngleBracketed(args) = &last.arguments else {
        return None;
    };
    let Some(syn::GenericArgument::Type(payload)) = args.args.first() else {
        return None;
    };
    Some(payload)
}

/// The parsed `(public, ret_names(...))` attribute arguments; ret_names is only legal on #[buck_export].
pub struct MacroArgs {
    pub public: bool,
    pub ret_names: Option<Vec<Ident>>,
}

pub fn parse_args(attr: TokenStream, allow_ret_names: bool) -> syn::Result<MacroArgs> {
    let mut args = MacroArgs {
        public: false,
        ret_names: None,
    };
    if attr.is_empty() {
        return Ok(args);
    }
    let metas: Punctuated<Meta, Token![,]> =
        syn::parse::Parser::parse2(Punctuated::parse_terminated, attr)?;
    for meta in metas {
        match &meta {
            Meta::Path(path) if path.is_ident("public") => {
                args.public = true;
            }
            Meta::List(list) if allow_ret_names && list.path.is_ident("ret_names") => {
                let names: Punctuated<Ident, Token![,]> =
                    list.parse_args_with(Punctuated::parse_terminated)?;
                args.ret_names = Some(names.into_iter().collect());
            }
            _ => {
                return Err(syn::Error::new_spanned(
                    meta,
                    if allow_ret_names {
                        "unsupported argument: expected `public` or `ret_names(...)`"
                    } else {
                        "unsupported argument: expected `public`"
                    },
                ));
            }
        }
    }
    Ok(args)
}

/// Doc lines from `///` comments, one leading space trimmed, for dump metadata (the emitter turns them into C# XML docs).
pub fn doc_lines(attrs: &[Attribute]) -> Vec<String> {
    let mut lines = Vec::new();
    for attr in attrs {
        if let Meta::NameValue(pair) = &attr.meta
            && pair.path.is_ident("doc")
            && let Expr::Lit(lit) = &pair.value
            && let Lit::Str(text) = &lit.lit
        {
            let text = text.value();
            lines.push(text.strip_prefix(' ').unwrap_or(&text).to_string());
        }
    }
    lines
}

/// True when the item carries `#[repr(...)]` containing exactly the given ident (e.g. "C", "i32").
pub fn has_repr(attrs: &[Attribute], wanted: &str) -> bool {
    for attr in attrs {
        if !attr.path().is_ident("repr") {
            continue;
        }
        let Ok(metas) = attr.parse_args_with(Punctuated::<Meta, Token![,]>::parse_terminated)
        else {
            continue;
        };
        for meta in metas {
            if let Meta::Path(path) = meta
                && path.is_ident(wanted)
            {
                return true;
            }
        }
    }
    false
}

/// Enum discriminant expression -> i64 (integer literal, optionally negated). Expr::Group unwraps the invisible delimiters a macro_rules `$value:literal` metavariable arrives in.
pub fn discriminant_value(expr: &Expr) -> syn::Result<i64> {
    match expr {
        Expr::Group(group) => discriminant_value(&group.expr),
        Expr::Paren(paren) => discriminant_value(&paren.expr),
        Expr::Lit(lit) => {
            if let Lit::Int(value) = &lit.lit {
                return value.base10_parse::<i64>();
            }
            Err(syn::Error::new_spanned(
                expr,
                "enum discriminants must be integer literals",
            ))
        }
        Expr::Unary(unary) => {
            if let UnOp::Neg(_) = unary.op {
                return Ok(-discriminant_value(&unary.expr)?);
            }
            Err(syn::Error::new_spanned(
                expr,
                "enum discriminants must be integer literals",
            ))
        }
        _ => Err(syn::Error::new_spanned(
            expr,
            "enum discriminants must be explicit integer literals",
        )),
    }
}

/// The always-on BuckMirrored assertion for a mirrored type mentioned in a signature: unmarked types fail every build, not just dump builds.
pub fn mirror_assertion(path: &syn::Path) -> TokenStream {
    quote! {
        const _: () = {
            const fn assert_mirrored<T: ::buckminster_core::ffi::BuckMirrored>() {}
            assert_mirrored::<#path>();
        };
    }
}

/// `&["line", "line"]` metadata tokens for doc lines.
pub fn docs_tokens(lines: &[String]) -> TokenStream {
    quote!(&[#(#lines),*])
}
