use buckminster_ffi_macros::buck_enum;

#[buck_enum]
#[repr(u32)]
enum Probe {
    A,
}

fn main() {}
