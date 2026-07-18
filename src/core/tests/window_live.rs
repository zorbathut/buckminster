//! Live windowing smoke test -- needs a real display server, so it is #[ignore]d and never runs in CI or test-all; run manually with `cargo test --test window_live -- --ignored` (cwd src/) on a machine with a display. Chunk 3's host-level verification supersedes this for the milestone done-when; this exists so the winit wrapper's create/pump/destroy path is exercised the moment it's written.

use buckminster_core::platform::PlatformEventRaw;

#[test]
#[ignore = "needs a display server; run manually"]
fn create_pump_destroy_round_trip() {
    let mut window: u64 = 0;
    let title = "buckminster window_live probe";
    let code = unsafe {
        buckminster_core::platform::buck_window_create(
            title.as_ptr(),
            title.len(),
            320,
            240,
            &mut window,
        )
    };
    assert_eq!(
        code, 0,
        "window create failed (is a display server available?)"
    );
    assert_ne!(window, 0);

    // A few pumps to let the compositor configure the window; poll everything that shows up.
    let mut seen_any = false;
    for _ in 0..20 {
        assert_eq!(buckminster_core::platform::buck_platform_pump(), 0);
        let mut buf = [PlatformEventRaw {
            window: 0,
            kind: 0,
            data0: 0,
            data1: 0,
            data2: 0,
        }; 32];
        let mut written: u32 = 0;
        let mut remaining: u32 = 0;
        let poll = unsafe {
            buckminster_core::platform::buck_platform_events_poll(
                buf.as_mut_ptr(),
                buf.len(),
                &mut written,
                &mut remaining,
            )
        };
        assert_eq!(poll, 0);
        for event in &buf[..written as usize] {
            assert_eq!(
                event.window, window,
                "events should attach to the one live window"
            );
            seen_any = true;
        }
        std::thread::sleep(std::time::Duration::from_millis(10));
    }

    let new_title = "buckminster window_live probe (retitled)";
    assert_eq!(
        unsafe {
            buckminster_core::platform::buck_window_set_title(
                window,
                new_title.as_ptr(),
                new_title.len(),
            )
        },
        0
    );

    let mut width: u32 = 0;
    let mut height: u32 = 0;
    assert_eq!(
        unsafe { buckminster_core::platform::buck_window_size(window, &mut width, &mut height) },
        0
    );
    assert!(
        width > 0 && height > 0,
        "window reports a real size ({width}x{height})"
    );

    assert_eq!(buckminster_core::platform::buck_window_destroy(window), 0);
    // Destroy again: stale handle must fail loudly, never alias.
    assert_ne!(buckminster_core::platform::buck_window_destroy(window), 0);

    // The compositor may or may not have delivered events in the window's brief life (Wayland often waits for a present); report rather than assert.
    println!("window_live: saw events during the probe: {seen_any}");
}
