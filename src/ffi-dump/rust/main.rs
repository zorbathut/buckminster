//! buckminster-ffi-dump: links every buck_*-exporting crate with its ffi-dump feature on, collects the inventory metadata that #[buck_export]/#[buck_struct]/#[buck_enum] registered, and writes it as sorted, deterministic JSON for the C# emitter (tools/lib/ffigen.py).

use serde::Serialize;

use buckminster_core::ffi::meta::{MetaEnum, MetaExport, MetaStruct, MetaTrait};
// Inventory submissions only exist in crates the binary actually links; an unreferenced dependency is dropped and its exports silently vanish from the dump.
use buckminster_host_desktop as _;

#[derive(Serialize)]
struct Dump {
    exports: Vec<&'static MetaExport>,
    structs: Vec<&'static MetaStruct>,
    enums: Vec<&'static MetaEnum>,
    traits: Vec<&'static MetaTrait>,
}

/// The full dump, sorted by (crate, name) at every level -- inventory iteration order is link-dependent, and the emitter's write-if-changed discipline needs byte-stable output.
fn dump_json() -> String {
    let mut exports: Vec<&'static MetaExport> =
        buckminster_core::ffi::inventory::iter::<MetaExport>().collect();
    exports.sort_by_key(|export| (export.crate_name, export.name));
    let mut structs: Vec<&'static MetaStruct> =
        buckminster_core::ffi::inventory::iter::<MetaStruct>().collect();
    structs.sort_by_key(|item| (item.crate_name, item.name));
    let mut enums: Vec<&'static MetaEnum> =
        buckminster_core::ffi::inventory::iter::<MetaEnum>().collect();
    enums.sort_by_key(|item| (item.crate_name, item.name));
    let mut traits: Vec<&'static MetaTrait> =
        buckminster_core::ffi::inventory::iter::<MetaTrait>().collect();
    traits.sort_by_key(|item| (item.crate_name, item.name));
    let dump = Dump {
        exports,
        structs,
        enums,
        traits,
    };
    let mut json =
        serde_json::to_string_pretty(&dump).expect("the meta types serialize infallibly");
    json.push('\n');
    json
}

fn main() {
    let mut args = std::env::args().skip(1);
    let (Some(path), None) = (args.next(), args.next()) else {
        eprintln!("usage: buckminster-ffi-dump <output-path>");
        std::process::exit(1);
    };
    std::fs::write(&path, dump_json())
        .unwrap_or_else(|error| panic!("failed to write {path}: {error}"));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parsed() -> serde_json::Value {
        serde_json::from_str(&dump_json()).expect("the dump is valid JSON")
    }

    fn names_of(value: &serde_json::Value, section: &str, key: &str) -> Vec<String> {
        value[section]
            .as_array()
            .expect("section is an array")
            .iter()
            .map(|entry| entry[key].as_str().expect("entry has the key").to_string())
            .collect()
    }

    #[test]
    fn dump_is_deterministic_and_sorted() {
        assert_eq!(dump_json(), dump_json());
        let value = parsed();
        for section in ["exports", "structs", "enums"] {
            let keys: Vec<(String, String)> = value[section]
                .as_array()
                .expect("section is an array")
                .iter()
                .map(|entry| {
                    (
                        entry["crate_name"]
                            .as_str()
                            .expect("entry has crate_name")
                            .to_string(),
                        entry["name"].as_str().expect("entry has name").to_string(),
                    )
                })
                .collect();
            let mut sorted = keys.clone();
            sorted.sort();
            assert_eq!(keys, sorted, "{section} not sorted by (crate_name, name)");
        }
    }

    #[test]
    fn dump_contains_the_pilot_exports() {
        let value = parsed();
        let symbols = names_of(&value, "exports", "symbol");
        for expected in [
            "buck_add",
            "buck_demesne_create",
            "buck_demesne_destroy",
            "buck_demesne_test_log_then_panic",
            "buck_demesne_test_panic",
            "buck_globals_init",
            "buck_globals_shutdown",
            "buck_globals_tick",
            "buck_test_panic",
        ] {
            assert!(
                symbols.iter().any(|symbol| symbol == expected),
                "missing export {expected} (have: {symbols:?})"
            );
        }
    }

    #[test]
    fn dump_records_the_pilot_raw_signature() {
        let value = parsed();
        let exports = value["exports"].as_array().expect("exports is an array");
        let probe = exports
            .iter()
            .find(|entry| entry["symbol"] == "buck_demesne_test_panic")
            .expect("buck_demesne_test_panic present");
        let raw: Vec<(String, String)> = probe["raw_params"]
            .as_array()
            .expect("raw_params is an array")
            .iter()
            .map(|param| {
                (
                    param["name"].as_str().expect("param name").to_string(),
                    param["ty"]["kind"]
                        .as_str()
                        .expect("param kind")
                        .to_string(),
                )
            })
            .collect();
        assert_eq!(raw, vec![("demesne".to_string(), "scalar".to_string())]);
        // The out-param/named-return encoding, pinned against buck_globals_tick (dt scalar in, tick_count out through a mut_ptr_scalar, return named).
        let tick = exports
            .iter()
            .find(|entry| entry["symbol"] == "buck_globals_tick")
            .expect("buck_globals_tick present");
        let tick_raw: Vec<(String, String)> = tick["raw_params"]
            .as_array()
            .expect("raw_params is an array")
            .iter()
            .map(|param| {
                (
                    param["name"].as_str().expect("param name").to_string(),
                    param["ty"]["kind"]
                        .as_str()
                        .expect("param kind")
                        .to_string(),
                )
            })
            .collect();
        assert_eq!(
            tick_raw,
            vec![
                ("dt".to_string(), "scalar".to_string()),
                ("out_tick_count".to_string(), "mut_ptr_scalar".to_string()),
            ]
        );
        assert_eq!(tick["returns"][0]["name"], "tick_count");
        // The raw layer is plain u64 (asserted above); semantically the handle keeps its kind so the emitter can grow typed C# handles later.
        assert_eq!(probe["params"][0]["ty"]["kind"], "rid");
        // Doc comments written ABOVE the attribute must still flow into the dump (attribute macros see sibling attrs); the emitter turns them into C# XML docs.
        assert!(
            !probe["docs"]
                .as_array()
                .expect("docs is an array")
                .is_empty(),
            "demesne_test_panic's doc comment should reach the dump"
        );
    }

    #[test]
    fn dump_contains_the_host_desktop_probe() {
        let value = parsed();
        let exports = value["exports"].as_array().expect("exports is an array");
        let probe = exports
            .iter()
            .find(|entry| entry["symbol"] == "buck_test_host_probe")
            .expect("buck_test_host_probe present");
        // The crate_name is what routes this export to the host-desktop assembly in ffigen's crate mapping; a wrong or missing name would silently merge it into core's bindings.
        assert_eq!(probe["crate_name"], "buckminster-host-desktop");
    }

    #[test]
    fn dump_contains_the_pilot_mirrors() {
        let value = parsed();
        let structs = names_of(&value, "structs", "name");
        assert!(
            structs.iter().any(|name| name == "EngineConfig"),
            "missing EngineConfig (have: {structs:?})"
        );
        let engine_config = value["structs"]
            .as_array()
            .expect("structs is an array")
            .iter()
            .find(|entry| entry["name"] == "EngineConfig")
            .expect("EngineConfig present");
        assert_eq!(engine_config["public"], true);
        assert_eq!(engine_config["fields"].as_array().expect("fields").len(), 3);

        let enums = value["enums"].as_array().expect("enums is an array");
        let ffi_code = enums
            .iter()
            .find(|entry| entry["name"] == "FfiCode")
            .expect("FfiCode present");
        assert_eq!(ffi_code["variants"].as_array().expect("variants").len(), 5);
        assert_eq!(ffi_code["repr"], "i32");
        let key_code = enums
            .iter()
            .find(|entry| entry["name"] == "KeyCode")
            .expect("KeyCode present");
        assert_eq!(
            key_code["variants"].as_array().expect("variants").len(),
            195
        );
        assert_eq!(key_code["public"], true);
    }
}
