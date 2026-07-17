//! #[buck_export] expansion: the idiomatic fn stays untouched; alongside it lands the `#[no_mangle] extern "C" buck_*` wrapper (M1's guard rails mechanized) and, under the exporting crate's ffi-dump feature, the inventory metadata the C# emitter consumes.

use proc_macro2::TokenStream;
use quote::{format_ident, quote};
use syn::{FnArg, Ident, ItemFn, Pat};

use crate::shared::{self, Elem, ParamKind, RetItem, Scalar};

/// Everything the expansion accumulates per raw extern parameter.
struct RawParam {
    decl: TokenStream,
    meta: TokenStream,
    is_pointer: bool,
}

pub fn expand(attr: TokenStream, item: TokenStream) -> syn::Result<TokenStream> {
    let args = shared::parse_args(attr, true)?;
    let func: ItemFn = syn::parse2(item)?;

    let sig = &func.sig;
    if !sig.generics.params.is_empty() || sig.generics.where_clause.is_some() {
        return Err(syn::Error::new_spanned(
            &sig.generics,
            "#[buck_export] fns cannot be generic",
        ));
    }
    if sig.constness.is_some()
        || sig.asyncness.is_some()
        || sig.unsafety.is_some()
        || sig.abi.is_some()
        || sig.variadic.is_some()
    {
        return Err(syn::Error::new_spanned(
            sig,
            "#[buck_export] fns are plain safe Rust fns",
        ));
    }

    let fn_name = sig.ident.clone();
    let name_str = fn_name.to_string();
    let symbol = format_ident!("buck_{}", fn_name);
    let symbol_str = symbol.to_string();

    // Returns first: they decide the out-param tail and the ret_names requirement.
    let shared::Returns {
        items: returns,
        fallible,
    } = shared::classify_returns(&sig.output)?;
    let ret_names: Vec<Ident> = match &args.ret_names {
        None if returns.is_empty() => Vec::new(),
        None if returns.len() == 1 => vec![format_ident!("value")],
        None => {
            return Err(syn::Error::new_spanned(
                &sig.output,
                "tuple returns require #[buck_export(ret_names(...))] so out-params and the C# named tuple get real names",
            ));
        }
        Some(names) => {
            if returns.is_empty() {
                return Err(syn::Error::new_spanned(
                    &sig.output,
                    "ret_names(...) given but the fn returns no value",
                ));
            }
            if names.len() != returns.len() {
                return Err(syn::Error::new_spanned(
                    &sig.output,
                    format!(
                        "ret_names(...) names {} values but the fn returns {}",
                        names.len(),
                        returns.len()
                    ),
                ));
            }
            names.clone()
        }
    };

    let mut raw_params: Vec<RawParam> = Vec::new();
    let mut preludes: Vec<TokenStream> = Vec::new();
    let mut call_args: Vec<Ident> = Vec::new();
    let mut meta_params: Vec<TokenStream> = Vec::new();
    let mut assertions: Vec<TokenStream> = Vec::new();

    for input in &sig.inputs {
        let FnArg::Typed(typed) = input else {
            return Err(syn::Error::new_spanned(
                input,
                "#[buck_export] fns take no self",
            ));
        };
        let Pat::Ident(pat) = &*typed.pat else {
            return Err(syn::Error::new_spanned(
                &typed.pat,
                "#[buck_export] parameters must be plain identifiers",
            ));
        };
        let name = pat.ident.clone();
        let name_string = name.to_string();
        let kind = shared::classify_param(&typed.ty)?;
        call_args.push(name.clone());

        match &kind {
            ParamKind::Scalar(scalar) => {
                let raw_ty = scalar.raw_tokens();
                let raw_scalar = scalar.raw_meta_tokens();
                let sem_scalar = scalar.meta_tokens();
                raw_params.push(RawParam {
                    decl: quote!(#name: #raw_ty),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaRawType::Scalar { scalar: #raw_scalar } }),
                    is_pointer: false,
                });
                if *scalar == Scalar::Bool {
                    preludes.push(quote!(let #name = #name != 0;));
                }
                meta_params.push(quote!(::buckminster_core::ffi::meta::MetaParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaType::Scalar { scalar: #sem_scalar } }));
            }
            ParamKind::Rid => {
                raw_params.push(RawParam {
                    decl: quote!(#name: u64),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaRawType::Scalar { scalar: ::buckminster_core::ffi::meta::MetaScalar::U64 } }),
                    is_pointer: false,
                });
                // Naming the real Rid type here is the loudness guarantee: an unrelated local type called Rid mismatches in the body and fails the build.
                preludes.push(quote!(let #name = ::buckminster_core::rid::Rid::from_raw(#name);));
                meta_params.push(quote!(::buckminster_core::ffi::meta::MetaParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaType::Rid }));
            }
            ParamKind::Str => {
                let ptr = format_ident!("{}_ptr", name);
                let len = format_ident!("{}_len", name);
                let ptr_string = ptr.to_string();
                let len_string = len.to_string();
                let context = format!("{symbol_str}: {name_string}");
                raw_params.push(RawParam {
                    decl: quote!(#ptr: *const u8),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #ptr_string, ty: ::buckminster_core::ffi::meta::MetaRawType::ConstPtrScalar { scalar: ::buckminster_core::ffi::meta::MetaScalar::U8 } }),
                    is_pointer: true,
                });
                raw_params.push(RawParam {
                    decl: quote!(#len: usize),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #len_string, ty: ::buckminster_core::ffi::meta::MetaRawType::Scalar { scalar: ::buckminster_core::ffi::meta::MetaScalar::USize } }),
                    is_pointer: false,
                });
                preludes.push(quote! {
                    let #name = unsafe { ::buckminster_core::ffi::utf8_arg(#ptr, #len, #context) }?;
                });
                meta_params.push(quote!(::buckminster_core::ffi::meta::MetaParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaType::Str }));
            }
            ParamKind::SliceIn(elem) | ParamKind::SliceOut(elem) => {
                let out = matches!(kind, ParamKind::SliceOut(_));
                let elem_ty = elem.rust_tokens();
                let elem_meta = elem.meta_tokens();
                let ptr = format_ident!("{}_ptr", name);
                let count = if out {
                    format_ident!("{}_cap", name)
                } else {
                    format_ident!("{}_len", name)
                };
                let ptr_string = ptr.to_string();
                let count_string = count.to_string();
                let message =
                    format!("{symbol_str}: {name_string}: null pointer with nonzero length");
                let ptr_meta = match (out, elem) {
                    (false, Elem::Scalar(scalar)) => {
                        let scalar = scalar.raw_meta_tokens();
                        quote!(::buckminster_core::ffi::meta::MetaRawType::ConstPtrScalar { scalar: #scalar })
                    }
                    (true, Elem::Scalar(scalar)) => {
                        let scalar = scalar.raw_meta_tokens();
                        quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrScalar { scalar: #scalar })
                    }
                    (false, Elem::Mirror(path)) => {
                        quote!(::buckminster_core::ffi::meta::MetaRawType::ConstPtrMirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME })
                    }
                    (true, Elem::Mirror(path)) => {
                        quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrMirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME })
                    }
                };
                let ptr_decl = if out {
                    quote!(#ptr: *mut #elem_ty)
                } else {
                    quote!(#ptr: *const #elem_ty)
                };
                raw_params.push(RawParam {
                    decl: ptr_decl,
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #ptr_string, ty: #ptr_meta }),
                    is_pointer: true,
                });
                raw_params.push(RawParam {
                    decl: quote!(#count: usize),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #count_string, ty: ::buckminster_core::ffi::meta::MetaRawType::Scalar { scalar: ::buckminster_core::ffi::meta::MetaScalar::USize } }),
                    is_pointer: false,
                });
                if out {
                    preludes.push(quote! {
                        let #name: &mut [#elem_ty] = if #count == 0 {
                            &mut []
                        } else if #ptr.is_null() {
                            return Err(::buckminster_core::ffi::FfiError::new(::buckminster_core::ffi::FfiCode::InvalidArgument, #message));
                        } else {
                            unsafe { ::std::slice::from_raw_parts_mut(#ptr, #count) }
                        };
                    });
                } else {
                    preludes.push(quote! {
                        let #name: &[#elem_ty] = if #count == 0 {
                            &[]
                        } else if #ptr.is_null() {
                            return Err(::buckminster_core::ffi::FfiError::new(::buckminster_core::ffi::FfiCode::InvalidArgument, #message));
                        } else {
                            unsafe { ::std::slice::from_raw_parts(#ptr, #count) }
                        };
                    });
                }
                if let Some(path) = elem.mirror_path() {
                    assertions.push(shared::mirror_assertion(path));
                }
                let meta_ty = if out {
                    quote!(::buckminster_core::ffi::meta::MetaType::SliceOut { elem: &#elem_meta })
                } else {
                    quote!(::buckminster_core::ffi::meta::MetaType::SliceIn { elem: &#elem_meta })
                };
                meta_params.push(
                    quote!(::buckminster_core::ffi::meta::MetaParam { name: #name_string, ty: #meta_ty }),
                );
            }
            ParamKind::MirrorRef(path) => {
                let message = format!("{symbol_str}: {name_string} is null");
                raw_params.push(RawParam {
                    decl: quote!(#name: *const #path),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaRawType::ConstPtrMirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME } }),
                    is_pointer: true,
                });
                preludes.push(quote! {
                    if #name.is_null() {
                        return Err(::buckminster_core::ffi::FfiError::new(::buckminster_core::ffi::FfiCode::InvalidArgument, #message));
                    }
                    let #name = unsafe { &*#name };
                });
                assertions.push(shared::mirror_assertion(path));
                meta_params.push(quote!(::buckminster_core::ffi::meta::MetaParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaType::Mirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME } }));
            }
            ParamKind::RefInOut(elem) => {
                let elem_ty = elem.rust_tokens();
                let elem_meta = elem.meta_tokens();
                let message = format!("{symbol_str}: {name_string} is null");
                let raw_meta = match elem {
                    Elem::Scalar(scalar) => {
                        let scalar = scalar.raw_meta_tokens();
                        quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrScalar { scalar: #scalar })
                    }
                    Elem::Mirror(path) => {
                        quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrMirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME })
                    }
                };
                raw_params.push(RawParam {
                    decl: quote!(#name: *mut #elem_ty),
                    meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #name_string, ty: #raw_meta }),
                    is_pointer: true,
                });
                preludes.push(quote! {
                    if #name.is_null() {
                        return Err(::buckminster_core::ffi::FfiError::new(::buckminster_core::ffi::FfiCode::InvalidArgument, #message));
                    }
                    let #name = unsafe { &mut *#name };
                });
                if let Some(path) = elem.mirror_path() {
                    assertions.push(shared::mirror_assertion(path));
                }
                meta_params.push(quote!(::buckminster_core::ffi::meta::MetaParam { name: #name_string, ty: ::buckminster_core::ffi::meta::MetaType::RefInOut { inner: &#elem_meta } }));
            }
        }
    }

    // Out-param tail + the call-and-write body. Out-params are non-null by caller contract (the standing convention); ptr::write avoids dropping whatever garbage the destination holds.
    let mut meta_returns: Vec<TokenStream> = Vec::new();
    let mut out_writes: Vec<TokenStream> = Vec::new();
    let mut tuple_locals: Vec<Ident> = Vec::new();
    for (index, (item, ret_name)) in returns.iter().zip(&ret_names).enumerate() {
        let ret_name_string = ret_name.to_string();
        let out_ident = format_ident!("out_{}", ret_name);
        let local = format_ident!("__value{}", index);
        tuple_locals.push(local.clone());
        let (out_ty, raw_meta, sem_meta, write_expr) = match item {
            RetItem::Scalar(scalar) => {
                let raw_ty = scalar.raw_tokens();
                let raw_scalar = scalar.raw_meta_tokens();
                let sem_scalar = scalar.meta_tokens();
                let write = if *scalar == Scalar::Bool {
                    quote!(#local as u8)
                } else {
                    quote!(#local)
                };
                (
                    raw_ty,
                    quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrScalar { scalar: #raw_scalar }),
                    quote!(::buckminster_core::ffi::meta::MetaType::Scalar { scalar: #sem_scalar }),
                    write,
                )
            }
            RetItem::Rid => (
                quote!(u64),
                quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrScalar {
                    scalar: ::buckminster_core::ffi::meta::MetaScalar::U64
                }),
                quote!(::buckminster_core::ffi::meta::MetaType::Rid),
                quote!(#local.raw()),
            ),
            RetItem::Mirror(path) => {
                assertions.push(shared::mirror_assertion(path));
                (
                    quote!(#path),
                    quote!(::buckminster_core::ffi::meta::MetaRawType::MutPtrMirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME }),
                    quote!(::buckminster_core::ffi::meta::MetaType::Mirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME }),
                    quote!(#local),
                )
            }
        };
        let out_string = out_ident.to_string();
        raw_params.push(RawParam {
            decl: quote!(#out_ident: *mut #out_ty),
            meta: quote!(::buckminster_core::ffi::meta::MetaRawParam { name: #out_string, ty: #raw_meta }),
            is_pointer: true,
        });
        meta_returns.push(
            quote!(::buckminster_core::ffi::meta::MetaReturn { name: #ret_name_string, ty: #sem_meta }),
        );
        out_writes.push(quote! {
            unsafe { ::std::ptr::write(#out_ident, #write_expr) };
        });
    }

    // Fallible bodies get `?` (their FfiError is the FFI-problem channel); plain bodies produce values directly -- the rails around them can still report Panic or a marshaling error.
    let try_suffix = if fallible { quote!(?) } else { quote!() };
    let call = match returns.len() {
        0 => quote! {
            #fn_name(#(#call_args),*) #try_suffix;
        },
        1 => {
            let local = &tuple_locals[0];
            quote! {
                let #local = #fn_name(#(#call_args),*) #try_suffix;
            }
        }
        _ => quote! {
            let (#(#tuple_locals),*) = #fn_name(#(#call_args),*) #try_suffix;
        },
    };

    let has_pointers = raw_params.iter().any(|param| param.is_pointer);
    let raw_decls: Vec<&TokenStream> = raw_params.iter().map(|param| &param.decl).collect();
    let raw_metas: Vec<&TokenStream> = raw_params.iter().map(|param| &param.meta).collect();

    let wrapper_doc = format!(" FFI wrapper for `{name_str}`, generated by #[buck_export].");
    let header = if has_pointers {
        quote! {
            #[doc = #wrapper_doc]
            #[doc = ""]
            #[doc = " # Safety"]
            #[doc = " Out-params must be non-null and writable (the standard out-param contract); input pointers follow the span/struct contracts (null only for zero-length spans)."]
            #[unsafe(no_mangle)]
            pub unsafe extern "C" fn #symbol(#(#raw_decls),*) -> i32
        }
    } else {
        quote! {
            #[doc = #wrapper_doc]
            #[unsafe(no_mangle)]
            pub extern "C" fn #symbol(#(#raw_decls),*) -> i32
        }
    };

    let docs = shared::docs_tokens(&shared::doc_lines(&func.attrs));
    let public = args.public;

    let metadata = quote! {
        #[cfg(feature = "ffi-dump")]
        ::buckminster_core::ffi::inventory::submit! {
            ::buckminster_core::ffi::meta::MetaExport {
                crate_name: ::core::env!("CARGO_PKG_NAME"),
                name: #name_str,
                symbol: #symbol_str,
                docs: #docs,
                public: #public,
                params: &[#(#meta_params),*],
                returns: &[#(#meta_returns),*],
                raw_params: &[#(#raw_metas),*],
            }
        }
    };

    Ok(quote! {
        #func

        #(#assertions)*

        #header {
            ::buckminster_core::ffi::guard(|| {
                #(#preludes)*
                #call
                #(#out_writes)*
                Ok(())
            })
        }

        #metadata
    })
}

#[cfg(test)]
mod tests {
    use proc_macro2::TokenStream;
    use quote::quote;

    // These tests pin the invariant the raw_params design exists for: the recorded raw metadata must describe the extern signature the macro actually generated, per vocabulary shape. The chunk-2 C# emitter consumes raw_params verbatim, so a decl/metadata disagreement here becomes a silent ABI mismatch there. Assertions match proc-macro2's token stringification (single spaces between tokens), which the pinned toolchain keeps stable.

    fn expand_string(attr: TokenStream, item: TokenStream) -> String {
        super::expand(attr, item)
            .expect("expansion should succeed")
            .to_string()
    }

    // raw_params is the LAST MetaExport field, so splitting at its key cleanly separates semantic metadata (before) from raw metadata (after).
    fn split_semantic_raw(expansion: &str) -> (&str, &str) {
        let start = expansion
            .find("raw_params :")
            .expect("metadata block present");
        (&expansion[..start], &expansion[start..])
    }

    #[test]
    fn bool_raw_layer_is_u8_in_decl_and_metadata() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(flag: bool) -> Result<bool, FfiError> {
                    Ok(flag)
                }
            },
        );
        assert!(
            expansion.contains("flag : u8"),
            "raw param decl: {expansion}"
        );
        assert!(
            expansion.contains("out_value : * mut u8"),
            "raw out decl: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(
            !raw.contains("MetaScalar :: Bool"),
            "bool must never appear in raw metadata: {raw}"
        );
        assert!(
            raw.contains("MetaScalar :: U8"),
            "raw metadata records the u8 that actually crosses: {raw}"
        );
        assert!(
            semantic.contains("MetaScalar :: Bool"),
            "semantic metadata keeps bool for the wrapper layer: {semantic}"
        );
    }

    #[test]
    fn str_raw_layer_is_ptr_len() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(text: &str) -> Result<(), FfiError> {
                    Ok(())
                }
            },
        );
        assert!(
            expansion.contains("text_ptr : * const u8 , text_len : usize"),
            "raw decls: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(
            raw.contains(
                "ConstPtrScalar { scalar : :: buckminster_core :: ffi :: meta :: MetaScalar :: U8 }"
            ),
            "raw ptr metadata: {raw}"
        );
        assert!(
            raw.contains("MetaScalar :: USize"),
            "raw len metadata: {raw}"
        );
        assert!(
            semantic.contains("MetaType :: Str"),
            "semantic metadata: {semantic}"
        );
    }

    #[test]
    fn scalar_slices_cross_as_ptr_and_len() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(values: &[u32]) -> Result<u64, FfiError> {
                    Ok(0)
                }
            },
        );
        assert!(
            expansion.contains("values_ptr : * const u32 , values_len : usize"),
            "raw decls: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(
            raw.contains(
                "ConstPtrScalar { scalar : :: buckminster_core :: ffi :: meta :: MetaScalar :: U32 }"
            ),
            "raw ptr metadata: {raw}"
        );
        assert!(
            semantic.contains("MetaType :: SliceIn"),
            "semantic metadata: {semantic}"
        );
    }

    #[test]
    fn mirror_slices_out_cross_as_mut_ptr_and_cap() {
        let expansion = expand_string(
            quote!(ret_names(written, remaining)),
            quote! {
                fn probe(buf: &mut [Pair]) -> Result<(u32, u32), FfiError> {
                    Ok((0, 0))
                }
            },
        );
        assert!(
            expansion.contains("buf_ptr : * mut Pair , buf_cap : usize"),
            "raw decls: {expansion}"
        );
        assert!(
            expansion.contains("out_written : * mut u32 , out_remaining : * mut u32"),
            "out-param tail order matches ret_names: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(raw.contains("MutPtrMirror"), "raw ptr metadata: {raw}");
        assert!(
            semantic.contains("MetaType :: SliceOut"),
            "semantic metadata: {semantic}"
        );
        assert!(
            semantic.contains(r#"MetaReturn { name : "written""#),
            "return names recorded: {semantic}"
        );
    }

    #[test]
    fn mirror_ref_crosses_as_const_ptr() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(pair: &Pair) -> Result<(), FfiError> {
                    Ok(())
                }
            },
        );
        assert!(
            expansion.contains("pair : * const Pair"),
            "raw decl: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(raw.contains("ConstPtrMirror"), "raw metadata: {raw}");
        assert!(
            semantic.contains("MetaType :: Mirror"),
            "semantic metadata: {semantic}"
        );
    }

    #[test]
    fn ref_in_out_crosses_as_mut_ptr() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(value: &mut u64) -> Result<(), FfiError> {
                    Ok(())
                }
            },
        );
        assert!(
            expansion.contains("value : * mut u64"),
            "raw decl: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(
            raw.contains(
                "MutPtrScalar { scalar : :: buckminster_core :: ffi :: meta :: MetaScalar :: U64 }"
            ),
            "raw metadata: {raw}"
        );
        assert!(
            semantic.contains("MetaType :: RefInOut"),
            "semantic metadata: {semantic}"
        );
    }

    #[test]
    fn struct_return_crosses_as_mut_ptr_out_param() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(a: u32) -> Result<Pair, FfiError> {
                    Ok(Pair { a, b: 0 })
                }
            },
        );
        assert!(
            expansion.contains("out_value : * mut Pair"),
            "raw out decl: {expansion}"
        );
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(raw.contains("MutPtrMirror"), "raw metadata: {raw}");
        assert!(
            semantic.contains("MetaType :: Mirror"),
            "semantic metadata: {semantic}"
        );
    }

    #[test]
    fn rid_crosses_as_u64_with_hidden_glue() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(engine: Rid) -> Result<Rid, FfiError> {
                    Ok(engine)
                }
            },
        );
        assert!(
            expansion.contains("engine : u64"),
            "raw param decl: {expansion}"
        );
        assert!(
            expansion.contains("out_value : * mut u64"),
            "raw out decl: {expansion}"
        );
        assert!(
            expansion.contains("Rid :: from_raw (engine)"),
            "param glue: {expansion}"
        );
        assert!(expansion.contains(". raw ()"), "return glue: {expansion}");
        let (semantic, raw) = split_semantic_raw(&expansion);
        assert!(
            raw.contains("MetaScalar :: U64"),
            "raw metadata is plain u64: {raw}"
        );
        assert!(
            !raw.contains("MetaType :: Rid"),
            "rid is a semantic kind, never a raw one: {raw}"
        );
        assert!(
            semantic.contains("MetaType :: Rid"),
            "semantic metadata: {semantic}"
        );
    }

    #[test]
    fn plain_form_calls_the_body_without_the_error_channel() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(a: u32) -> u64 {
                    u64::from(a) * 2
                }
            },
        );
        assert!(
            expansion.contains("out_value : * mut u64"),
            "raw out decl: {expansion}"
        );
        assert!(
            expansion.contains("probe (a) ;"),
            "plain body call, no ?: {expansion}"
        );
        assert!(
            !expansion.contains("probe (a) ? ;"),
            "plain body call, no ?: {expansion}"
        );
    }

    #[test]
    fn usize_param_stays_usize_at_the_raw_layer() {
        let expansion = expand_string(
            TokenStream::new(),
            quote! {
                fn probe(count: usize) -> Result<(), FfiError> {
                    Ok(())
                }
            },
        );
        assert!(expansion.contains("count : usize"), "raw decl: {expansion}");
        let (_, raw) = split_semantic_raw(&expansion);
        assert!(
            raw.contains(
                "Scalar { scalar : :: buckminster_core :: ffi :: meta :: MetaScalar :: USize }"
            ),
            "raw metadata: {raw}"
        );
    }
}
