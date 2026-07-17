use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export]
fn probe(flag: &mut bool) -> Result<(), MyErr> {
    let _ = flag;
    Ok(())
}

fn main() {}
