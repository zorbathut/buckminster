use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export(frobnicate)]
fn probe() -> Result<(), MyErr> {
    Ok(())
}

fn main() {}
