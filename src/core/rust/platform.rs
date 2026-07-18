//! The desktop platform layer (PLAN.md M5): winit wrapped in pump mode behind flat buck_platform_*/buck_window_* exports. Events buffer as plain #[repr(C)] records and C# PULLS them via bulk poll -- no Rust->C# callback ever fires from in here, which is what keeps callback rule 4 trivially honest against winit's own dispatch reentrancy (a deliberate deviation from PLAN's original "window events -> C# callbacks" sketch; the PLAN annotation lands with the M5 close-out).
//!
//! The platform layer is a PROCESS SINGLETON (one winit event loop, one event buffer) and is THREAD-BOUND: the first platform call claims the calling thread, every later platform call must come from it (winit's EventLoop is !Send; the loop is built with with_any_thread so "owner" is simply whoever called first). This is stricter than the engine's general threading model; ARCHITECTURE.md's threading section carries the contract.
//!
//! On targets without winit (emscripten -- its wasm backend is wasm-bindgen-only -- or feature "window" off) the same symbols export as loud stubs returning InvalidArgument: the wasm static link resolves the pinvoke table by name and fails on undefined symbols, so the export surface must be target-uniform (the binding-generation deferred entry pins the same rule).
//!
//! Error-code stance, written down deliberately: unavailability (no display server, stub target, dead loop) reports as InvalidArgument with a descriptive message rather than a dedicated code -- per errors-are-bugs, a host that wants to fall back headless PROGRAMMATICALLY needs a capability query (Godot's has_feature analog, future work), not error-code probing.

use crate::ffi::{FfiCode, FfiError, buck_export, buck_struct};
use crate::rid::Rid;

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

#[cfg(all(feature = "window", not(target_os = "emscripten")))]
mod native {
    use std::cell::RefCell;
    use std::collections::HashMap;
    use std::num::NonZeroU32;
    use std::rc::Rc;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::Duration;

    use winit::application::ApplicationHandler;
    use winit::dpi::PhysicalSize;
    use winit::event::{ElementState, WindowEvent};
    use winit::event_loop::{ActiveEventLoop, EventLoop};
    use winit::keyboard::PhysicalKey;
    use winit::window::{Window, WindowId};

    use super::*;
    use crate::keycode::keycode_from_winit;
    use crate::rid::{Rid, RidAllocator};

    const TAG_WINDOW: u8 = 2;

    // Thread affinity: winit's EventLoop is !Send, so all platform state lives in a thread_local and an atomic owner token makes any wrong-thread call a loud error instead of an invisible second event loop. Thread tokens are process-unique counters (ThreadId::as_u64 is unstable).
    static NEXT_THREAD_TOKEN: AtomicU64 = AtomicU64::new(1);
    static OWNER_TOKEN: AtomicU64 = AtomicU64::new(0);
    thread_local! {
        static THREAD_TOKEN: u64 = NEXT_THREAD_TOKEN.fetch_add(1, Ordering::Relaxed);
        static PLATFORM: RefCell<Option<PlatformState>> = const { RefCell::new(None) };
    }

    struct PlatformState {
        event_loop: EventLoop<()>,
        app: App,
        // Set when a pump reports PumpStatus::Exit: the loop is dead, later window creates must fail loudly instead of queueing forever.
        exited: bool,
    }

    struct CommandCreate {
        token: u64,
        title: String,
        width: u32,
        height: u32,
    }

    // softbuffer 0RGB: dark slate -- obviously a window, obviously not rendering yet.
    const CLEAR_COLOR: u32 = 0x0020_2430;

    // A live window plus its interim clear presenter. Field order is drop order: surface before context before window. The presenter exists because Wayland MAPS a window only once a buffer is committed -- without a present, the window is a taskbar entry with no surface on screen (found live at M5); M6's WebGPU surface replaces the fill, the in-callback redraw handling is the permanent shape.
    struct WindowEntry {
        surface: softbuffer::Surface<Rc<Window>, Rc<Window>>,
        _context: softbuffer::Context<Rc<Window>>,
        window: Rc<Window>,
    }

    impl WindowEntry {
        // A present failure is diagnostics, not a crash: log and let the next redraw retry.
        fn present_clear(&mut self) {
            let size = self.window.inner_size();
            let (Some(width), Some(height)) =
                (NonZeroU32::new(size.width), NonZeroU32::new(size.height))
            else {
                return;
            };
            let result = self.surface.resize(width, height).and_then(|()| {
                let mut buffer = self.surface.buffer_mut()?;
                buffer.fill(CLEAR_COLOR);
                buffer.present()
            });
            if let Err(error) = result {
                log::warn!("window clear present failed: {error}");
            }
        }
    }

    struct App {
        windows: RidAllocator<WindowEntry>,
        by_winit_id: HashMap<WindowId, Rid>,
        events: Vec<PlatformEventRaw>,
        create_queue: Vec<CommandCreate>,
        create_results: Vec<(u64, Result<Rid, String>)>,
        next_create_token: u64,
        // Deliberately ONE field, not per-window, even though winit addresses ModifiersChanged to a window: the keyboard is one physical device, key events only reach the focused window, and winit re-delivers modifier state on focus change, so device-global self-corrects. (A ModifiersChanged for a just-destroyed window is dropped by the unknown-window gate above the match -- also self-correcting at the next focus.)
        modifiers: u32,
    }

    impl App {
        fn new() -> App {
            App {
                windows: RidAllocator::new(TAG_WINDOW),
                by_winit_id: HashMap::new(),
                events: Vec::new(),
                create_queue: Vec::new(),
                create_results: Vec::new(),
                next_create_token: 1,
                modifiers: 0,
            }
        }

        fn push(&mut self, window: Rid, kind: i32, data0: u32, data1: u32, data2: u32) {
            self.events.push(PlatformEventRaw {
                kind,
                window: window.raw(),
                data0,
                data1,
                data2,
            });
        }
    }

    impl ApplicationHandler for App {
        fn resumed(&mut self, _event_loop: &ActiveEventLoop) {
            // Desktop fires this once at startup and window creation happens via the command queue in about_to_wait; Android-style surface lifecycles are a future-platform concern. NOTE the M6 cliff: winit requires RedrawRequested/Suspended/Resumed to be handled synchronously inside the callback, NOT buffered like our event set -- the render surface work must come back here.
        }

        fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
            // The only place &ActiveEventLoop exists, hence the only place windows can be created; buck_window_create queues a command and pumps once to flush through here.
            for command in self.create_queue.drain(..) {
                let attributes = Window::default_attributes()
                    .with_title(&command.title)
                    .with_inner_size(PhysicalSize::new(command.width, command.height));
                match event_loop.create_window(attributes) {
                    Ok(window) => {
                        let window = Rc::new(window);
                        let winit_id = window.id();
                        match softbuffer::Context::new(window.clone()).and_then(|context| {
                            softbuffer::Surface::new(&context, window.clone())
                                .map(|surface| (context, surface))
                        }) {
                            Ok((context, surface)) => {
                                // The first present happens on the RedrawRequested this triggers; until then the window is unmapped on Wayland.
                                window.request_redraw();
                                let rid = self.windows.insert(WindowEntry {
                                    surface,
                                    _context: context,
                                    window,
                                });
                                self.by_winit_id.insert(winit_id, rid);
                                self.create_results.push((command.token, Ok(rid)));
                            }
                            Err(error) => {
                                self.create_results.push((
                                    command.token,
                                    Err(format!("clear presenter setup failed: {error}")),
                                ));
                            }
                        }
                    }
                    Err(error) => {
                        self.create_results
                            .push((command.token, Err(error.to_string())));
                    }
                }
            }
        }

        fn window_event(
            &mut self,
            _event_loop: &ActiveEventLoop,
            window_id: WindowId,
            event: WindowEvent,
        ) {
            let Some(&rid) = self.by_winit_id.get(&window_id) else {
                // Queued events can outlive an explicit destroy by a pump; nothing to attach them to, and C# never learns the id. Debug-visible, not silent.
                log::debug!(
                    "platform event for unknown window {window_id:?} dropped (destroyed last pump?)"
                );
                return;
            };
            match event {
                WindowEvent::RedrawRequested => {
                    // Serviced synchronously in-callback per winit's pump contract (the noted M6 cliff, arrived early via the clear presenter).
                    if let Some(entry) = self.windows.get_mut(rid) {
                        entry.present_clear();
                    }
                }
                WindowEvent::Resized(size) => {
                    if let Some(entry) = self.windows.get(rid) {
                        entry.window.request_redraw();
                    }
                    self.push(rid, EVENT_KIND_RESIZED, size.width, size.height, 0);
                }
                WindowEvent::CloseRequested => {
                    // Advisory only (Godot's cooperative close): C# decides whether to destroy; the platform layer never tears down a window on its own.
                    self.push(rid, EVENT_KIND_CLOSE_REQUESTED, 0, 0, 0);
                }
                WindowEvent::Focused(focused) => {
                    self.push(rid, EVENT_KIND_FOCUS_CHANGED, focused as u32, 0, 0);
                }
                WindowEvent::ModifiersChanged(modifiers) => {
                    let state = modifiers.state();
                    self.modifiers = (if state.shift_key() { MODIFIER_SHIFT } else { 0 })
                        | (if state.control_key() {
                            MODIFIER_CONTROL
                        } else {
                            0
                        })
                        | (if state.alt_key() { MODIFIER_ALT } else { 0 })
                        | (if state.super_key() { MODIFIER_META } else { 0 });
                }
                WindowEvent::KeyboardInput { event, .. } => {
                    let keycode = match event.physical_key {
                        PhysicalKey::Code(code) => keycode_from_winit(code),
                        PhysicalKey::Unidentified(_) => crate::keycode::KeyCode::Unidentified,
                    };
                    let flags = (if event.state == ElementState::Pressed {
                        KEY_FLAG_PRESSED
                    } else {
                        0
                    }) | (if event.repeat { KEY_FLAG_REPEAT } else { 0 });
                    self.push(rid, EVENT_KIND_KEY, keycode as u32, flags, self.modifiers);
                }
                _ => {}
            }
        }
    }

    fn claim_or_verify_owner() -> Result<(), FfiError> {
        let token = THREAD_TOKEN.with(|t| *t);
        match OWNER_TOKEN.compare_exchange(0, token, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => Ok(()),
            Err(owner) if owner == token => Ok(()),
            Err(_) => Err(FfiError::new(
                FfiCode::InvalidArgument,
                "platform windowing is thread-bound: all buck_platform_*/buck_window_* calls must come from the thread that made the first one",
            )),
        }
    }

    // Runs body with the platform state, creating the event loop on first use. The RefCell stays borrowed across the body, so platform exports must never re-enter each other -- same shape as the engine registry's lock discipline.
    fn with_platform<R>(
        body: impl FnOnce(&mut PlatformState) -> Result<R, FfiError>,
    ) -> Result<R, FfiError> {
        claim_or_verify_owner()?;
        PLATFORM.with(|cell| {
            let mut slot = cell.borrow_mut();
            if slot.is_none() {
                let mut builder = EventLoop::builder();
                // with_any_thread: the atomic owner token is the real guard; winit's own main-thread assertion would otherwise panic when the host's first platform call happens to run off the process main thread (legal under our contract). macOS has no such override and hard-requires the main thread -- a documented future-platform note.
                #[cfg(target_os = "linux")]
                {
                    use winit::platform::wayland::EventLoopBuilderExtWayland;
                    use winit::platform::x11::EventLoopBuilderExtX11;
                    EventLoopBuilderExtWayland::with_any_thread(&mut builder, true);
                    EventLoopBuilderExtX11::with_any_thread(&mut builder, true);
                }
                #[cfg(target_os = "windows")]
                {
                    use winit::platform::windows::EventLoopBuilderExtWindows;
                    EventLoopBuilderExtWindows::with_any_thread(&mut builder, true);
                }
                let event_loop = builder.build().map_err(|error| FfiError::new(FfiCode::InvalidArgument, format!("platform event loop creation failed (headless environment / no display server? winit selects Wayland when WAYLAND_DISPLAY is set, else X11 via DISPLAY): {error}")))?;
                *slot = Some(PlatformState { event_loop, app: App::new(), exited: false });
            }
            body(slot.as_mut().expect("just initialized above"))
        })
    }

    fn pump_once(state: &mut PlatformState) {
        use winit::platform::pump_events::{EventLoopExtPumpEvents, PumpStatus};
        if state.exited {
            return;
        }
        if let PumpStatus::Exit(_) = state
            .event_loop
            .pump_app_events(Some(Duration::ZERO), &mut state.app)
        {
            state.exited = true;
        }
    }

    pub fn pump() -> Result<(), FfiError> {
        with_platform(|state| {
            pump_once(state);
            if state.exited {
                // A dead loop must not pump green forever -- that's the banned silent-swallow shape. events_poll stays functional so the buffered tail can still drain.
                return Err(FfiError::new(
                    FfiCode::InvalidArgument,
                    "the platform event loop has exited and cannot be pumped again",
                ));
            }
            Ok(())
        })
    }

    pub fn events_poll(buf: &mut [PlatformEventRaw]) -> Result<(u32, u32), FfiError> {
        with_platform(|state| {
            let take = buf.len().min(state.app.events.len());
            for (slot, event) in buf.iter_mut().zip(state.app.events.drain(..take)) {
                *slot = event;
            }
            Ok((take as u32, state.app.events.len() as u32))
        })
    }

    pub fn window_create(title: &str, width: u32, height: u32) -> Result<Rid, FfiError> {
        with_platform(|state| {
            let token = state.app.next_create_token;
            state.app.next_create_token += 1;
            state.app.create_queue.push(CommandCreate {
                token,
                title: title.to_string(),
                width,
                height,
            });
            // Flush the command through about_to_wait immediately so creation is synchronous; the pump may buffer unrelated events, which simply poll out later.
            pump_once(state);
            if let Some(index) = state
                .app
                .create_results
                .iter()
                .position(|(t, _)| *t == token)
            {
                match state.app.create_results.swap_remove(index).1 {
                    Ok(rid) => Ok(rid),
                    Err(message) => Err(FfiError::new(
                        FfiCode::InvalidArgument,
                        format!("window creation failed: {message}"),
                    )),
                }
            } else {
                // The command never drained: the loop exited (PumpStatus::Exit) or the pump never reached about_to_wait. Loud, never a garbage RID.
                state
                    .app
                    .create_queue
                    .retain(|command| command.token != token);
                Err(FfiError::new(
                    FfiCode::InvalidArgument,
                    "window creation did not complete: the platform event loop is no longer running",
                ))
            }
        })
    }

    pub fn window_destroy(rid: Rid) -> Result<(), FfiError> {
        with_platform(|state| {
            match state.app.windows.remove(rid) {
                Ok(entry) => {
                    state.app.by_winit_id.remove(&entry.window.id());
                    // Dropping the entry (surface, then context, then the winit Window) closes it.
                    drop(entry);
                    Ok(())
                }
                Err(why) => {
                    let raw = rid.raw();
                    Err(FfiError::new(
                        FfiCode::InvalidArgument,
                        format!("window destroy: invalid handle {raw:#x}: {why:?}"),
                    ))
                }
            }
        })
    }

    pub fn window_set_title(rid: Rid, title: &str) -> Result<(), FfiError> {
        with_platform(|state| match state.app.windows.get(rid) {
            Some(entry) => {
                entry.window.set_title(title);
                Ok(())
            }
            None => {
                let raw = rid.raw();
                Err(FfiError::new(
                    FfiCode::InvalidArgument,
                    format!("window set_title: invalid handle {raw:#x}"),
                ))
            }
        })
    }

    pub fn window_size(rid: Rid) -> Result<(u32, u32), FfiError> {
        with_platform(|state| match state.app.windows.get(rid) {
            Some(entry) => {
                let size = entry.window.inner_size();
                Ok((size.width, size.height))
            }
            None => {
                let raw = rid.raw();
                Err(FfiError::new(
                    FfiCode::InvalidArgument,
                    format!("window size: invalid handle {raw:#x}"),
                ))
            }
        })
    }
}

#[cfg(not(all(feature = "window", not(target_os = "emscripten"))))]
mod native {
    use super::*;

    fn unavailable() -> FfiError {
        FfiError::new(
            FfiCode::InvalidArgument,
            "platform windowing is not available in this build (wasm has no Rust windowing layer -- see PLAN.md's web-event-path entry -- and a no-default-features native build compiles these same stubs)",
        )
    }

    pub fn pump() -> Result<(), FfiError> {
        Err(unavailable())
    }

    pub fn events_poll(_buf: &mut [PlatformEventRaw]) -> Result<(u32, u32), FfiError> {
        Err(unavailable())
    }

    pub fn window_create(_title: &str, _width: u32, _height: u32) -> Result<Rid, FfiError> {
        Err(unavailable())
    }

    pub fn window_destroy(_rid: Rid) -> Result<(), FfiError> {
        Err(unavailable())
    }

    pub fn window_set_title(_rid: Rid, _title: &str) -> Result<(), FfiError> {
        Err(unavailable())
    }

    pub fn window_size(_rid: Rid) -> Result<(u32, u32), FfiError> {
        Err(unavailable())
    }
}

/// Drives the platform event loop exactly one non-blocking pump. See the module doc for the thread-affinity contract.
#[buck_export]
fn platform_pump() -> Result<(), FfiError> {
    native::pump()
}

/// Polls buffered platform events into `buf`: returns how many were written and how many remain buffered (per-frame bulk pull; no callback ever fires from the platform layer).
#[buck_export(ret_names(written, remaining))]
fn platform_events_poll(buf: &mut [PlatformEventRaw]) -> Result<(u32, u32), FfiError> {
    native::events_poll(buf)
}

/// Creates a native window and returns its handle. Creation is synchronous: the command queues into winit's about_to_wait and is flushed by an immediate pump, and a command that cannot drain (dead loop) is a loud error, never a garbage handle.
#[buck_export(ret_names(window))]
fn window_create(title: &str, width: u32, height: u32) -> Result<Rid, FfiError> {
    native::window_create(title, width, height)
}

/// Destroys a native window. Cooperative close: CloseRequested is advisory, this is what actually tears down.
#[buck_export]
fn window_destroy(window: Rid) -> Result<(), FfiError> {
    native::window_destroy(window)
}

/// Sets a native window's title.
#[buck_export]
fn window_set_title(window: Rid, title: &str) -> Result<(), FfiError> {
    native::window_set_title(window, title)
}

/// Reads a native window's inner (client) size in physical pixels.
#[buck_export(ret_names(width, height))]
fn window_size(window: Rid) -> Result<(u32, u32), FfiError> {
    native::window_size(window)
}

/// Exhaustive keycode-sync probe: writes the Rust-side name for `value` into `buf` and returns its byte length (InvalidArgument for values outside the enum, and a too-small buffer is a loud error rather than a silent truncation). Permanent test export, buck_test_* family.
#[buck_export(ret_names(length))]
fn test_keycode_name(value: u32, buf: &mut [u8]) -> Result<usize, FfiError> {
    let Some(code) = crate::keycode::KeyCode::from_raw(value) else {
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
    crate::keycode::KeyCode::COUNT
}

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
