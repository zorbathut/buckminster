use buckminster_ffi_macros::buck_struct;

#[buck_struct]
#[repr(C)]
struct Probe {
    flag: bool,
}

fn main() {}
