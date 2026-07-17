use buckminster_ffi_macros::buck_export;

struct MyErr;

#[buck_export]
fn probe(text: &mut str) -> Result<(), MyErr> {
    let _ = text;
    Ok(())
}

fn main() {}
