use buckminster_ffi_macros::buck_trait;

struct MyErr;

#[buck_trait]
trait Probe {
    fn poke(&mut self, buf: &mut [u8]) -> Result<(), MyErr>;
}

fn main() {}
