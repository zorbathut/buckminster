use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export(ret_names(value))]
fn probe() -> Result<(), MyErr> {
    Ok(())
}

fn main() {}
