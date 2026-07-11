# Changelog
All notable changes to this project will be documented in this file.


## [unreleased]

### Added

- Single tool entry point: `./tool.bat <command>` (polyglot sh+batch, runs everywhere), with a self-healing poetry-managed tools venv and a `tools/<command>.py` command registry. Commands: `build`, `test`, `check`.
- Toolchain audit/auto-provisioning (`tools/lib/toolchains.py`): rustup and the .NET SDK are installed user-local when missing (Windows paths untested until the M5 CI build check); C compiler / MSVC Build Tools are audited with install instructions. Pins: `rust-toolchain.toml`, `global.json`, `tools/poetry.lock`.
- SCons build orchestration with pyright strict mode enforced on the build code (`./tool.bat check`); the SCons API is confined behind the typed facade `tools/lib/sconsfacade.py` (checked at basic mode, the quarantine for SCons type suppressions), with the logic-free `SConstruct` shim unchecked.
- Repo skeleton under `src/` organized by module with language subdirs: `src/core` (Rust crate `buckminster-core`), `src/stdcs` (C# assembly `Buckminster`, NUnit tests), `src/host-desktop` (desktop host executable). Placeholder code only, replaced from M1 on. The root directory stays a map of the repo — entry points and docs only; cross-module build files (Cargo workspace, `rust-toolchain.toml`, `Directory.Build.props`) live in `src/`, pyright config in `tools/`, with `global.json` root-bound by SDK discovery rules.
- ARCHITECTURE.md: governing design principle, tier stack, repo layout, build tooling, and the pinned engine-wide conventions (coordinates, units, glam semantics, depth range, `[closed, open)` ranges, vendoring provenance).
- FFI walking skeleton (M1): `buckminster-core` now builds as a cdylib, with the standard entry-point rails every future export uses — `ffi::guard` (`catch_unwind`, error-code returns, out-param results) and `buck_last_error_message()` (thread-local, non-destructive read) — proven by the first exports `buck_add`, `buck_callback_invoke`, and the permanent panic-containment probe `buck_test_panic`.
- C# FFI layer (`Buckminster.Ffi`, internal): hand-written `[LibraryImport]` bindings under the native names, the mirrored `FfiCode` enum, `CallbackTable` — the u64-userdata-key registry backing the `[UnmanagedCallersOnly]` callback convention — and `CallbackExceptionStash`, the per-thread take-semantics channel that carries a callback's caught exception back to the original caller. Tests cover the add/error/panic paths, the callback round trip including exception containment in both directions, and callback reentry into the FFI.
- Native-library wiring: the cargo-built `.so` is copied into .NET output directories transitively, `dotnet build` fails with an instructive error when cargo hasn't run, and `./tool.bat build` orders the Rust build before the .NET build.
- Wasm walking skeleton (M2): the Rust core builds as a `wasm32-unknown-emscripten` staticlib (`build-rust-wasm` target) and links into two new wasm hosts via `NativeFileReference` — `src/host-web` (browser page) and `src/host-wasmnode` (Node terminal, the `wasm-desktop` target) — both running the full M1 FFI round trip including callback and panic containment (`catch_unwind` works under wasm-EH on all three targets).
- `docs/wasm-toolchain.md`: the four-component matched set (rustc / .NET SDK+workload / emscripten / Node), the failure classes actually hit at M2 with their fixes, workload-emsdk discovery, the template channel, and the bump re-verification checklist.
- Toolchain audit now checks the .NET wasm-tools workload (instructions on failure, warn-only emscripten drift tripwire); `rust-toolchain.toml` pins the wasm target so rustup auto-installs it.
- `./tool.bat test` passes `--no-build` to `dotnet test` (SCons already built the identical configuration; previously the solution built twice per test run).

### Fixed

- `dotnet publish -c Release` of the browser host no longer fails in wasm-opt: the Release link is pinned to `-O1` (skipping the binaryen pass that rejects rustc's newer target_features). The publish output is verified static-servable (`python3 -m http.server`); deployment shape documented in docs/wasm-toolchain.md.

### Breaking

- `NativeMethods.buck_callback_invoke` takes the callback as `IntPtr` instead of `delegate* unmanaged<ulong, int, int*, int>` (ABI-identical; call sites cast). Mono's wasm interp-to-native path aborts on function-pointer parameter types in pinvoke signatures, so this is now a standing binding convention: callbacks cross the FFI as `IntPtr`.
