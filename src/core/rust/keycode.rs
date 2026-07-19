//! Buckminster's raw-key vocabulary: physical (layout-independent) key codes mirroring the W3C UI Events `code` set. One macro LIST is the single source of truth: `keycode_list!` (exported) feeds both this module's enum/name/from_raw and the host-desktop crate's winit translation (winit's `KeyCode` also mirrors W3C, so that mapping is name-identical; core itself has no winit dependency since the M5.75 hoist -- the winit column here is just identifiers to core). The C# mirror is verified exhaustively against `buck_test_keycode_name` rather than trusted.
//!
//! Discriminants are sequential and arbitrary but STABLE: they cross the FFI inside PlatformEventRaw and will someday live in recorded input traces, so reordering or renumbering is a breaking change, and new codes append.

use crate::ffi::{FfiCode, FfiError, buck_export};

// One entry per key: (VariantName, discriminant, WinitVariantName). This consumer generates the enum, name(), from_raw(), and COUNT; the host-desktop crate's winit_mapping consumer generates the translation from the SAME list via keycode_list! below -- a single list nothing can drift from. The winit column is explicit because the names are ALMOST all identical by shared W3C ancestry: winit alone calls the OS key Super (SuperLeft/SuperRight) where W3C and we say Meta.
macro_rules! keycodes {
    ($(($name:ident, $value:literal, $winit:ident)),* $(,)?) => {
        /// Physical key code (W3C UI Events `code` semantics): identifies the key's position, not its layout-dependent meaning. `Unidentified = 0` is the loud fallback for anything the platform layer can't name.
        #[crate::ffi::buck_enum(public)]
        #[repr(u32)]
        #[derive(Clone, Copy, PartialEq, Eq, Debug)]
        pub enum KeyCode {
            Unidentified = 0,
            $($name = $value,)*
        }

        impl KeyCode {
            /// Total number of codes including Unidentified -- the C# mirror's count is checked against this.
            pub const COUNT: u32 = 1 + [$($value),*].len() as u32;

            pub fn name(self) -> &'static str {
                match self {
                    KeyCode::Unidentified => "Unidentified",
                    $(KeyCode::$name => stringify!($name),)*
                }
            }

            pub fn from_raw(raw: u32) -> Option<KeyCode> {
                match raw {
                    0 => Some(KeyCode::Unidentified),
                    $($value => Some(KeyCode::$name),)*
                    _ => None,
                }
            }
        }
    };
}

// The exported single-source list (callback style): invoke as `keycode_list!(your_macro)` and your macro receives every (VariantName, discriminant, WinitVariantName) triple. host-desktop's winit translation consumes this, so the list can never fork across crates.
#[macro_export]
macro_rules! keycode_list {
    ($callback:ident) => {
        $callback! {
        (Backquote, 1, Backquote),
        (Backslash, 2, Backslash),
        (BracketLeft, 3, BracketLeft),
        (BracketRight, 4, BracketRight),
        (Comma, 5, Comma),
        (Digit0, 6, Digit0),
        (Digit1, 7, Digit1),
        (Digit2, 8, Digit2),
        (Digit3, 9, Digit3),
        (Digit4, 10, Digit4),
        (Digit5, 11, Digit5),
        (Digit6, 12, Digit6),
        (Digit7, 13, Digit7),
        (Digit8, 14, Digit8),
        (Digit9, 15, Digit9),
        (Equal, 16, Equal),
        (IntlBackslash, 17, IntlBackslash),
        (IntlRo, 18, IntlRo),
        (IntlYen, 19, IntlYen),
        (KeyA, 20, KeyA),
        (KeyB, 21, KeyB),
        (KeyC, 22, KeyC),
        (KeyD, 23, KeyD),
        (KeyE, 24, KeyE),
        (KeyF, 25, KeyF),
        (KeyG, 26, KeyG),
        (KeyH, 27, KeyH),
        (KeyI, 28, KeyI),
        (KeyJ, 29, KeyJ),
        (KeyK, 30, KeyK),
        (KeyL, 31, KeyL),
        (KeyM, 32, KeyM),
        (KeyN, 33, KeyN),
        (KeyO, 34, KeyO),
        (KeyP, 35, KeyP),
        (KeyQ, 36, KeyQ),
        (KeyR, 37, KeyR),
        (KeyS, 38, KeyS),
        (KeyT, 39, KeyT),
        (KeyU, 40, KeyU),
        (KeyV, 41, KeyV),
        (KeyW, 42, KeyW),
        (KeyX, 43, KeyX),
        (KeyY, 44, KeyY),
        (KeyZ, 45, KeyZ),
        (Minus, 46, Minus),
        (Period, 47, Period),
        (Quote, 48, Quote),
        (Semicolon, 49, Semicolon),
        (Slash, 50, Slash),
        (AltLeft, 51, AltLeft),
        (AltRight, 52, AltRight),
        (Backspace, 53, Backspace),
        (CapsLock, 54, CapsLock),
        (ContextMenu, 55, ContextMenu),
        (ControlLeft, 56, ControlLeft),
        (ControlRight, 57, ControlRight),
        (Enter, 58, Enter),
        (MetaLeft, 59, SuperLeft),
        (MetaRight, 60, SuperRight),
        (ShiftLeft, 61, ShiftLeft),
        (ShiftRight, 62, ShiftRight),
        (Space, 63, Space),
        (Tab, 64, Tab),
        (Convert, 65, Convert),
        (KanaMode, 66, KanaMode),
        (Lang1, 67, Lang1),
        (Lang2, 68, Lang2),
        (Lang3, 69, Lang3),
        (Lang4, 70, Lang4),
        (Lang5, 71, Lang5),
        (NonConvert, 72, NonConvert),
        (Delete, 73, Delete),
        (End, 74, End),
        (Help, 75, Help),
        (Home, 76, Home),
        (Insert, 77, Insert),
        (PageDown, 78, PageDown),
        (PageUp, 79, PageUp),
        (ArrowDown, 80, ArrowDown),
        (ArrowLeft, 81, ArrowLeft),
        (ArrowRight, 82, ArrowRight),
        (ArrowUp, 83, ArrowUp),
        (NumLock, 84, NumLock),
        (Numpad0, 85, Numpad0),
        (Numpad1, 86, Numpad1),
        (Numpad2, 87, Numpad2),
        (Numpad3, 88, Numpad3),
        (Numpad4, 89, Numpad4),
        (Numpad5, 90, Numpad5),
        (Numpad6, 91, Numpad6),
        (Numpad7, 92, Numpad7),
        (Numpad8, 93, Numpad8),
        (Numpad9, 94, Numpad9),
        (NumpadAdd, 95, NumpadAdd),
        (NumpadBackspace, 96, NumpadBackspace),
        (NumpadClear, 97, NumpadClear),
        (NumpadClearEntry, 98, NumpadClearEntry),
        (NumpadComma, 99, NumpadComma),
        (NumpadDecimal, 100, NumpadDecimal),
        (NumpadDivide, 101, NumpadDivide),
        (NumpadEnter, 102, NumpadEnter),
        (NumpadEqual, 103, NumpadEqual),
        (NumpadHash, 104, NumpadHash),
        (NumpadMemoryAdd, 105, NumpadMemoryAdd),
        (NumpadMemoryClear, 106, NumpadMemoryClear),
        (NumpadMemoryRecall, 107, NumpadMemoryRecall),
        (NumpadMemoryStore, 108, NumpadMemoryStore),
        (NumpadMemorySubtract, 109, NumpadMemorySubtract),
        (NumpadMultiply, 110, NumpadMultiply),
        (NumpadParenLeft, 111, NumpadParenLeft),
        (NumpadParenRight, 112, NumpadParenRight),
        (NumpadStar, 113, NumpadStar),
        (NumpadSubtract, 114, NumpadSubtract),
        (Escape, 115, Escape),
        (Fn, 116, Fn),
        (FnLock, 117, FnLock),
        (PrintScreen, 118, PrintScreen),
        (ScrollLock, 119, ScrollLock),
        (Pause, 120, Pause),
        (BrowserBack, 121, BrowserBack),
        (BrowserFavorites, 122, BrowserFavorites),
        (BrowserForward, 123, BrowserForward),
        (BrowserHome, 124, BrowserHome),
        (BrowserRefresh, 125, BrowserRefresh),
        (BrowserSearch, 126, BrowserSearch),
        (BrowserStop, 127, BrowserStop),
        (Eject, 128, Eject),
        (LaunchApp1, 129, LaunchApp1),
        (LaunchApp2, 130, LaunchApp2),
        (LaunchMail, 131, LaunchMail),
        (MediaPlayPause, 132, MediaPlayPause),
        (MediaSelect, 133, MediaSelect),
        (MediaStop, 134, MediaStop),
        (MediaTrackNext, 135, MediaTrackNext),
        (MediaTrackPrevious, 136, MediaTrackPrevious),
        (Power, 137, Power),
        (Sleep, 138, Sleep),
        (AudioVolumeDown, 139, AudioVolumeDown),
        (AudioVolumeMute, 140, AudioVolumeMute),
        (AudioVolumeUp, 141, AudioVolumeUp),
        (WakeUp, 142, WakeUp),
        (Meta, 143, Meta),
        (Hyper, 144, Hyper),
        (Turbo, 145, Turbo),
        (Abort, 146, Abort),
        (Resume, 147, Resume),
        (Suspend, 148, Suspend),
        (Again, 149, Again),
        (Copy, 150, Copy),
        (Cut, 151, Cut),
        (Find, 152, Find),
        (Open, 153, Open),
        (Paste, 154, Paste),
        (Props, 155, Props),
        (Select, 156, Select),
        (Undo, 157, Undo),
        (Hiragana, 158, Hiragana),
        (Katakana, 159, Katakana),
        (F1, 160, F1),
        (F2, 161, F2),
        (F3, 162, F3),
        (F4, 163, F4),
        (F5, 164, F5),
        (F6, 165, F6),
        (F7, 166, F7),
        (F8, 167, F8),
        (F9, 168, F9),
        (F10, 169, F10),
        (F11, 170, F11),
        (F12, 171, F12),
        (F13, 172, F13),
        (F14, 173, F14),
        (F15, 174, F15),
        (F16, 175, F16),
        (F17, 176, F17),
        (F18, 177, F18),
        (F19, 178, F19),
        (F20, 179, F20),
        (F21, 180, F21),
        (F22, 181, F22),
        (F23, 182, F23),
        (F24, 183, F24),
        (F25, 184, F25),
        (F26, 185, F26),
        (F27, 186, F27),
        (F28, 187, F28),
        (F29, 188, F29),
        (F30, 189, F30),
        (F31, 190, F31),
        (F32, 191, F32),
        (F33, 192, F33),
        (F34, 193, F34),
        (F35, 194, F35),
        }
    };
}

crate::keycode_list!(keycodes);

/// Exhaustive keycode-sync probe: writes the Rust-side name for `value` into `buf` and returns its byte length (InvalidArgument for values outside the enum, and a too-small buffer is a loud error rather than a silent truncation). Permanent test export, buck_test_* family.
#[buck_export(ret_names(length))]
fn test_keycode_name(value: u32, buf: &mut [u8]) -> Result<usize, FfiError> {
    let Some(code) = KeyCode::from_raw(value) else {
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            format!("no keycode with value {value}"),
        ));
    };
    let name = code.name().as_bytes();
    if buf.len() < name.len() {
        return Err(FfiError::new(
            FfiCode::InvalidArgument,
            format!(
                "keycode name: buffer too small ({} < {} bytes)",
                buf.len(),
                name.len()
            ),
        ));
    }
    buf[..name.len()].copy_from_slice(name);
    Ok(name.len())
}

/// The keycode count for the C# mirror's both-ways check.
#[buck_export(ret_names(count))]
fn test_keycode_count() -> u32 {
    KeyCode::COUNT
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn from_raw_roundtrips_every_code() {
        let mut found = 0;
        for raw in 0..KeyCode::COUNT {
            let code =
                KeyCode::from_raw(raw).expect("discriminants are sequential 0..COUNT with no gaps");
            assert_eq!(code as u32, raw);
            found += 1;
        }
        assert_eq!(found, KeyCode::COUNT);
        assert!(KeyCode::from_raw(KeyCode::COUNT).is_none());
        assert!(KeyCode::from_raw(u32::MAX).is_none());
    }

    #[test]
    fn names_are_unique_and_nonempty() {
        let mut seen = std::collections::HashSet::new();
        for raw in 0..KeyCode::COUNT {
            let name = KeyCode::from_raw(raw).unwrap().name();
            assert!(!name.is_empty());
            assert!(seen.insert(name), "duplicate keycode name {name}");
        }
    }

    #[test]
    fn known_values_are_pinned() {
        // A few load-bearing anchors pinned by value: these appear in recorded traces someday, so renumbering must fail a test even though the C# mirror check would also catch it.
        assert_eq!(KeyCode::Unidentified as u32, 0);
        assert_eq!(KeyCode::KeyA as u32, 20);
        assert_eq!(KeyCode::Space as u32, 63);
        assert_eq!(KeyCode::Escape as u32, 115);
        assert_eq!(KeyCode::F35 as u32, 194);
    }
}
