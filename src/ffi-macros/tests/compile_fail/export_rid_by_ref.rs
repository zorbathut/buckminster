use buckminster_ffi_macros::buck_export;

struct Rid(u64);

struct MyErr;

#[buck_export]
fn probe(engine: &Rid) -> Result<(), MyErr> {
    let _ = engine;
    Ok(())
}

fn main() {}
