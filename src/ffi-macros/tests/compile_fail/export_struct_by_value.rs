// Since enum-by-value params joined the vocabulary, a bare struct param is indistinguishable from an enum at the syntax level, so the fence moved into the type system: in a real crate this fails on the `Config: BuckEnum` bound. This sandbox has no buckminster-core dependency, so the pinned error is the earlier missing-crate resolution failure -- still loud, differently worded.
use buckminster_ffi_macros::buck_export;

struct Config {
    value: i32,
}

struct MyErr;

#[buck_export]
fn probe(config: Config) -> Result<i32, MyErr> {
    Ok(config.value)
}

fn main() {}
