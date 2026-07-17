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
