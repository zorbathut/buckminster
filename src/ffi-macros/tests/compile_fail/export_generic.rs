use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export]
fn probe<T>(value: i32) -> Result<i32, MyErr> {
    Ok(value)
}

fn main() {}
