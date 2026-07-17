use buckminster_ffi_macros::buck_struct;

#[buck_struct]
#[repr(C)]
struct Probe(u32, u32);

fn main() {}
