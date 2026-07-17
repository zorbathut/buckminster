use buckminster_ffi_macros::buck_export;

#[buck_export]
fn probe(self) -> Result<(), ()> {
    Ok(())
}

fn main() {}
