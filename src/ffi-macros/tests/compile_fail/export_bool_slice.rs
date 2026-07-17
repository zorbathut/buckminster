use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export]
fn probe(flags: &[bool]) -> Result<(), MyErr> {
    let _ = flags;
    Ok(())
}

fn main() {}
