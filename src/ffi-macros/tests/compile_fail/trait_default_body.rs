use buckminster_ffi_macros::buck_trait;

struct MyErr;

#[buck_trait]
trait Probe {
    fn poke(&mut self, value: i32) -> Result<i32, MyErr> {
        Ok(value)
    }
}

fn main() {}
