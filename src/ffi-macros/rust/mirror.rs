//! #[buck_struct] / #[buck_enum] expansion: the item stays untouched; alongside it land the BuckMirrored marker (always) and, under the exporting crate's ffi-dump feature, the BuckMirror identity impl plus inventory metadata for the C# emitter.

use proc_macro2::TokenStream;
use quote::quote;
use syn::{Fields, ItemEnum, ItemStruct};

use crate::shared::{self, Scalar};

pub fn expand_struct(attr: TokenStream, item: TokenStream) -> syn::Result<TokenStream> {
    let args = shared::parse_args(attr, false)?;
    let item: ItemStruct = syn::parse2(item)?;
    if !shared::has_repr(&item.attrs, "C") {
        return Err(syn::Error::new_spanned(
            &item.ident,
            "#[buck_struct] requires #[repr(C)] (place #[buck_struct] ABOVE #[repr(C)] so it can see it)",
        ));
    }
    if !item.generics.params.is_empty() {
        return Err(syn::Error::new_spanned(
            &item.generics,
            "#[buck_struct] types cannot be generic",
        ));
    }
    let Fields::Named(fields) = &item.fields else {
        return Err(syn::Error::new_spanned(
            &item.ident,
            "#[buck_struct] requires named fields",
        ));
    };

    let name = item.ident.clone();
    let name_str = name.to_string();
    let mut meta_fields: Vec<TokenStream> = Vec::new();
    let mut assertions: Vec<TokenStream> = Vec::new();
    for field in &fields.named {
        let field_name = field
            .ident
            .as_ref()
            .expect("named fields have idents")
            .to_string();
        let field_docs = shared::docs_tokens(&shared::doc_lines(&field.attrs));
        let ty = match (Scalar::from_type(&field.ty), &field.ty) {
            (Some(Scalar::Bool), _) => {
                return Err(syn::Error::new_spanned(
                    &field.ty,
                    "bool fields are outside the FFI vocabulary (no C-ABI-agreed width in a mirrored struct); use u8",
                ));
            }
            (Some(scalar), _) => {
                let scalar = scalar.meta_tokens();
                quote!(::buckminster_core::ffi::meta::MetaType::Scalar { scalar: #scalar })
            }
            (None, syn::Type::Path(path)) => {
                let path = &path.path;
                assertions.push(shared::mirror_assertion(path));
                quote!(::buckminster_core::ffi::meta::MetaType::Mirror { crate_name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::CRATE, name: <#path as ::buckminster_core::ffi::meta::BuckMirror>::NAME })
            }
            _ => {
                return Err(syn::Error::new_spanned(
                    &field.ty,
                    "unsupported field type: mirrored struct fields are scalars or other mirrored structs",
                ));
            }
        };
        meta_fields.push(quote!(::buckminster_core::ffi::meta::MetaField { name: #field_name, ty: #ty, docs: #field_docs }));
    }

    let docs = shared::docs_tokens(&shared::doc_lines(&item.attrs));
    let public = args.public;
    Ok(quote! {
        #item

        impl ::buckminster_core::ffi::BuckMirrored for #name {}

        #(#assertions)*

        #[cfg(feature = "ffi-dump")]
        impl ::buckminster_core::ffi::meta::BuckMirror for #name {
            const CRATE: &'static str = ::core::env!("CARGO_PKG_NAME");
            const NAME: &'static str = #name_str;
        }

        #[cfg(feature = "ffi-dump")]
        ::buckminster_core::ffi::inventory::submit! {
            ::buckminster_core::ffi::meta::MetaStruct {
                crate_name: ::core::env!("CARGO_PKG_NAME"),
                name: #name_str,
                docs: #docs,
                public: #public,
                fields: &[#(#meta_fields),*],
            }
        }
    })
}

pub fn expand_enum(attr: TokenStream, item: TokenStream) -> syn::Result<TokenStream> {
    let args = shared::parse_args(attr, false)?;
    let item: ItemEnum = syn::parse2(item)?;
    let repr = if shared::has_repr(&item.attrs, "i32") {
        Scalar::I32
    } else if shared::has_repr(&item.attrs, "u32") {
        Scalar::U32
    } else {
        return Err(syn::Error::new_spanned(
            &item.ident,
            "#[buck_enum] requires #[repr(i32)] or #[repr(u32)] (place #[buck_enum] ABOVE the repr so it can see it)",
        ));
    };

    let name = item.ident.clone();
    let name_str = name.to_string();
    let mut meta_variants: Vec<TokenStream> = Vec::new();
    for variant in &item.variants {
        if !matches!(variant.fields, Fields::Unit) {
            return Err(syn::Error::new_spanned(
                &variant.ident,
                "#[buck_enum] variants must be unit variants",
            ));
        }
        let Some((_, discriminant)) = &variant.discriminant else {
            return Err(syn::Error::new_spanned(
                &variant.ident,
                "#[buck_enum] variants need explicit discriminants",
            ));
        };
        let value = shared::discriminant_value(discriminant)?;
        let variant_name = variant.ident.to_string();
        let variant_docs = shared::docs_tokens(&shared::doc_lines(&variant.attrs));
        meta_variants.push(quote!(::buckminster_core::ffi::meta::MetaVariant { name: #variant_name, value: #value, docs: #variant_docs }));
    }

    let docs = shared::docs_tokens(&shared::doc_lines(&item.attrs));
    let public = args.public;
    let repr_meta = repr.meta_tokens();
    Ok(quote! {
        #item

        impl ::buckminster_core::ffi::BuckMirrored for #name {}

        #[cfg(feature = "ffi-dump")]
        impl ::buckminster_core::ffi::meta::BuckMirror for #name {
            const CRATE: &'static str = ::core::env!("CARGO_PKG_NAME");
            const NAME: &'static str = #name_str;
        }

        #[cfg(feature = "ffi-dump")]
        ::buckminster_core::ffi::inventory::submit! {
            ::buckminster_core::ffi::meta::MetaEnum {
                crate_name: ::core::env!("CARGO_PKG_NAME"),
                name: #name_str,
                docs: #docs,
                public: #public,
                repr: #repr_meta,
                variants: &[#(#meta_variants),*],
            }
        }
    })
}
