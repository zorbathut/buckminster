use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export(ret_names(a, b, c))]
fn probe() -> Result<(u32, u32), MyErr> {
    Ok((0, 0))
}

fn main() {}
