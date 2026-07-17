use buckminster_ffi_macros::buck_export;

struct Rid(u64);

struct MyErr;

#[buck_export]
fn probe(handles: &[Rid]) -> Result<(), MyErr> {
    let _ = handles;
    Ok(())
}

fn main() {}
