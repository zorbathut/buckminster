use buckminster_ffi_macros::buck_trait;

#[buck_trait]
trait Probe {
    fn poke(&mut self, value: i32) -> i32;
}

fn main() {}
