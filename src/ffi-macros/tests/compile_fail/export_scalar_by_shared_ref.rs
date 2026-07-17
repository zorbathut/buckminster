use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export]
fn probe(value: &u32) -> Result<u32, MyErr> {
    Ok(*value)
}

fn main() {}
