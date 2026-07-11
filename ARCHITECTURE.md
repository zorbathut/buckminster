# Buckminster Architecture

Buckminster is a microkernel-inspired game engine: a Rust core under a flat C FFI, C# above it, shipped as a library that hosts embed. This document records the load-bearing decisions and conventions; the milestone-by-milestone build-out lives in PLAN.md.

## Governing principle: the default must be simple

The default way to write anything must be simple — sacrificing power and performance for simplicity where they conflict. Power and performance are retrofit paths: when a specific piece of a game needs to be fast, that piece gets fast-and-hard-ified, and the rest of the game stays easy. Mixed-speed codebases are the expected permanent state, not a transition, so the obligation cuts both ways: the easy path must have escape hatches reachable from inside it, and the fast path must tolerate slow code living next to it in the same game and the same frame. (Unity's DOTS-era ECS is the named anti-example: its fast-capable model was also fast-mandatory, which dragged every trivial thing into the hard model the moment one system needed speed.)

## Tier stack

Hosts own the loop: the engine is a callee exposing `PumpEvents()` / `Tick(dt)` / `Render()`, and a desktop executable, a browser rAF callback, a Node script, or a test harness drives it. Rust owns platform glue, windowing, the low-level WebGPU rendering layer, and kernel primitives (RIDs, PRNG, logging transport), exported as flat `extern "C"` functions consumed via `[LibraryImport]`. C# owns the module registry, the engine object, the mesh/pass/dispatch layer, and everything above — code migrates C#→Rust only when profiling demands it. Three first-class run targets: `linux` (native), `web` (browser, emscripten), and `wasm-desktop` (the same emscripten build hosted by Node). Determinism is a hard engine property: same inputs, same order, fixed dt, deterministic game code ⇒ bit-identical sim state (full rules in PLAN.md's Determinism section).

## FFI

Everything crossing the Rust↔C# boundary is flat `extern "C"` with blittable args, hand-written bindings on both sides.

**Entry-point wrapper**: every fallible `buck_*` export is a thin `extern "C"` fn whose body runs inside `ffi::guard` (`src/core/rust/ffi.rs`) — `catch_unwind` so no panic ever crosses the boundary, error-to-code mapping, and last-error storage. Exports return an `i32` `FfiCode` and write results through out-params. Out-params are non-null by caller contract and **unspecified on a nonzero return** — check the code before reading. Callback function pointers share the non-null contract, and more sharply: Rust declares them as non-nullable `fn` types, so a null there is an invalid value at the ABI boundary, not merely a pointer that must not be dereferenced. Codes: `0 Ok, 1 Panic, 2 CallbackError, 3 InvalidArgument` — defined in `ffi.rs`, hand-mirrored in `src/stdcs/cs/Ffi/FfiCode.cs`, kept in sync manually; new codes append.

**`buck_last_error_message()`**: null if the last `buck_*` call on the calling thread succeeded; otherwise the error message, valid until the next `buck_*` call *other than `buck_last_error_message` itself* on the same thread — reading is non-destructive and repeatable. Callers copy immediately (`NativeMethods.LastErrorMessage()` does). This is the one deliberate exception to "every export runs inside guard": it is infallible, and routing it through the guard would clear the very error it exists to read.

**Callback rules** (from PLAN.md, non-negotiable): (1) callbacks are `[UnmanagedCallersOnly]` static methods passed as function pointers, never marshalled delegates; (2) no exception ever escapes a callback — every callback body is a try/catch wrapper; (3) static callback + u64 userdata key, no closures — `CallbackTable` (`src/stdcs/cs/Ffi/CallbackTable.cs`) is the userdata registry, and the `[UnmanagedCallersOnly]` static recovers its target object from it by key; (4) Rust never holds a lock or borrow across a callback invocation. Plus the rail those four don't cover — **how a callback failure's detail reaches the original caller**: the callback's catch stashes the exception in `CallbackExceptionStash` (`src/stdcs/cs/Ffi/CallbackExceptionStash.cs` — per-thread, take-semantics so a stale exception can't be misattributed later) and returns nonzero; Rust maps any nonzero callback return to `CallbackError` (preserving the value in the message); the original caller sees `CallbackError`, takes the stash, and rethrows. Callbacks mirror the export shape: `i32` code return, result via out-param.

**Bindings** (`src/stdcs/cs/Ffi/NativeMethods.cs`): `[LibraryImport]`, keeping the native snake_case names for 1:1 greppability against the Rust exports — the friendly PascalCase layer is the M4 Engine's job. Raw FFI stays `internal`, never public API; tests reach it via `InternalsVisibleTo`.

**Native library wiring**: `src/stdcs/cs/Buckminster.csproj` copies `src/target/debug/libbuckminster_core.so` into the output dir (`None` + `CopyToOutputDirectory`, flowing transitively to test and host projects), with an explicit loud build error naming `./tool.bat build` when cargo hasn't produced it (skipped in IDE design-time builds so project load doesn't half-fail). The build tooling orders `build-dotnet` after `build-rust` for the same reason.

## Repo layout

The root directory is a map of the repo, kept uncluttered: entry points (`tool.bat`, `SConstruct`, `Buckminster.slnx`), user-facing documentation, and the `src/` and `tools/` trees. Infrastructure files live down inside the tree they serve, not at the root. The one exception is `global.json`: the .NET SDK resolver and IDEs discover it by walking up from wherever `dotnet` runs, so it must sit at (or above) the solution — moving it into `src/` would silently unpin root-invoked builds. It also carries the `dotnet test` runner opt-in (Microsoft.Testing.Platform).

Source lives under `src/`, organized by module — not by language. `src/` itself holds the cross-module build files: the Cargo workspace (`Cargo.toml`, `Cargo.lock` — discovered by walking up from the crates), `rust-toolchain.toml` (discovered differently: rustup walks up from the *cwd of the cargo invocation*, so cargo must run with cwd inside `src/` — the build tooling does, and CI steps must too; a root-cwd invocation using `--manifest-path` would silently use the machine's default toolchain instead of the pin), and the shared MSBuild properties (`Directory.Build.props`, discovered by walk-up from each project). IDE note: JetBrains attaches the nested Cargo workspace automatically; VS Code's rust-analyzer needs `rust-analyzer.linkedProjects` pointed at `src/Cargo.toml`, and pylance won't auto-find the pyright config (`[tool.pyright]` in `tools/lib/pyproject.toml`) from a root workspace — `./tool.bat check` remains the enforcement path regardless. A module directory contains language subdirs: `rust/` (crate root via a `[lib] path` override in the module's `Cargo.toml`), `cs/` (one C# project), `tests/` (test projects; Rust unit tests live in the crate, and cargo integration tests would also land in `tests/` next to `Cargo.toml`). A module may be single-language; hosts follow the same convention.

Current modules:

- `src/core` — Rust. Kernel primitives; currently the FFI walking skeleton (entry-point wrapper rails plus the `buck_add`/callback/panic proof exports; real kernel surface arrives from M4 on).
- `src/stdcs` — C#. The managed core library; assembly and root namespace `Buckminster`. Namespaces grow by topic (`Buckminster.Render`, …), not by module — the module split is a repo-organization detail.
- `src/host-desktop` — C#. The desktop host executable (`Buckminster.Host.Desktop` assembly, `Buckminster` namespace consumer).

Tests are NUnit (run through the Microsoft.Testing.Platform runner; NUnitLite is the natural in-host runner candidate when the M3 wasm test matrix lands).

## Build tooling

One entry point: `./tool.bat <command>` (the same file runs under sh and cmd). It verifies and self-heals the tools environment (a poetry-managed venv at `tools/lib/.venv`), then dispatches to `tools/<command>.py`. Every `tools/*.py` is a command — support code and env config live in `tools/lib/`, imported as `from lib import ...`. Current commands: `build`, `test`, `check`.

- **Install floor**: git, Python 3.10+, and platform C++ build tools (MSVC Build Tools on Windows — Rust's linker needs them anyway). Everything else is auto-provisioned user-local (rustup into `~/.cargo`/`~/.rustup`, the .NET SDK into `~/.dotnet` when no satisfying SDK exists) or audited with exact install instructions (`tools/lib/toolchains.py`).
- **Pins**: `rust-toolchain.toml` (exact Rust version; honored because cargo is always resolved through rustup, never trusted from PATH), `global.json` (.NET SDK band; arbitration is always delegated to the SDK resolver itself), `tools/lib/poetry.lock` (python tooling), package versions in csproj files.
- **Orchestration**: SCons owns the coarse meta-DAG (tool invocations, and later the shader pipeline, wasm link assembly, codegen, packaging); cargo and MSBuild remain the fine-grained inner build systems. All build code is Python checked by pyright (`./tool.bat check`, CI-enforced from M3) in strict mode, with exactly two scoped exemptions: the root `SConstruct` (an untyped logic-free shim — SCons injects globals pyright can't model) and `tools/lib/sconsfacade.py` (checked at basic mode — it's the quarantine for all SCons type suppressions, since SCons ships no type information, and the migration seam if SCons is ever replaced). Target definitions live in `tools/lib/buildtargets.py`, fully strict, touching SCons only through the facade.
- rustfmt and clippy run with default configurations — no config files; gates land with CI in M3.

## Conventions

Pinned before line one of math code; uniform everywhere:

- **Coordinates**: right-handed, **Y-up**, −Z forward (glTF-compatible).
- **Units**: meters, seconds, radians.
- **Matrix/vector semantics**: glam conventions (column vectors, column-major storage) on the Rust side; C# mirrors glam semantics. `System.Numerics` types may be used for storage/SIMD, but the documented convention is glam's — any transposition responsibility gets pinned in writing at the boundary that incurs it.
- **Depth**: 0..1 clip range (WebGPU native). Reversed-Z is deferred but anticipated: nothing may bake in "near = 0".
- **Ranges are `[closed, open)`**, uniformly. Anything needing inclusive semantics converts at the call site, never inside shared utilities. Adjacent ranges must not double-count their shared edge; the uniformity is the point.
- **Vendored/forked code carries provenance**: a `VERSION.txt` recording the source repo + commit, why it's forked, what changed, and a "re-import from upstream rather than reimplement" pointer — plus symmetric `// BUCK BEGIN <desc>` / `// BUCK END <desc>` markers around every local patch to vendored code, so patches survive upstream upgrades.
