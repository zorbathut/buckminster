use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export]
fn probe() -> Result<(u32, u32), MyErr> {
    Ok((0, 0))
}

fn main() {}
