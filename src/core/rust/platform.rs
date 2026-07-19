//! The platform EVENT VOCABULARY -- the wire shapes any host's platform layer produces and C# consumes. The desktop implementation (winit wrapped in pump mode, the process-singleton thread-bound event loop, the buck_platform_*/buck_window_* exports) lives in the buckminster-host-desktop crate since the M5.75 hoist: platform code is host territory, and hoisting it is what lets the wasm builds carry no windowing symbols at all (no stubs -- the wasm executor simply never links the desktop platform crate).

use crate::ffi::buck_struct;

/// The wire shape of one platform event. data0..data2 are per-kind: Resized(width, height, -); Key(keycode, key flags, modifiers); FocusChanged(0/1, -, -); CloseRequested(-, -, -). Field order is deliberate: the u64 first keeps the struct padding-free (24 bytes) -- kind-first would pad to 32.
#[buck_struct]
#[repr(C)]
#[derive(Clone, Copy)]
pub struct PlatformEventRaw {
    pub window: u64,
    pub kind: i32,
    pub data0: u32,
    pub data1: u32,
    pub data2: u32,
}

pub const EVENT_KIND_RESIZED: i32 = 1;
pub const EVENT_KIND_CLOSE_REQUESTED: i32 = 2;
pub const EVENT_KIND_FOCUS_CHANGED: i32 = 3;
pub const EVENT_KIND_KEY: i32 = 4;

pub const KEY_FLAG_PRESSED: u32 = 1;
pub const KEY_FLAG_REPEAT: u32 = 2;

pub const MODIFIER_SHIFT: u32 = 1;
pub const MODIFIER_CONTROL: u32 = 2;
pub const MODIFIER_ALT: u32 = 4;
pub const MODIFIER_META: u32 = 8;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn event_struct_layout_is_what_the_seam_reports() {
        // The layout export is itself trivially derived; this pins the actual packed shape (u64-first, padding-free) so an accidental field reorder is caught rust-side too.
        assert_eq!(std::mem::size_of::<PlatformEventRaw>(), 24);
        assert_eq!(std::mem::offset_of!(PlatformEventRaw, window), 0);
        assert_eq!(std::mem::offset_of!(PlatformEventRaw, kind), 8);
        assert_eq!(std::mem::offset_of!(PlatformEventRaw, data0), 12);
        assert_eq!(std::mem::offset_of!(PlatformEventRaw, data1), 16);
        assert_eq!(std::mem::offset_of!(PlatformEventRaw, data2), 20);
    }
}
