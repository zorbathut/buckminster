# The wasm toolchain: pins, coupling, and re-verification

The Rust core reaches the two wasm run targets by being built as a `wasm32-unknown-emscripten` staticlib and linked into the .NET wasm build (`NativeFileReference`, `src/WasmHost.props`). That link couples **four** independently-versioned components; this document records the matched set, why each coupling exists, and the checklist for bumping anything. The governing rule, from PLAN.md: **verification means linking and running the skeleton, never comparing version strings.**

## The matched set (verified together, M2)

| Component | Pinned at | Pinned by | Coupling |
|---|---|---|---|
| rustc | 1.96.1 | `src/rust-toolchain.toml` | Ships a *prebuilt* `wasm32-unknown-emscripten` std, built against rustc-CI's own emscripten (newer than ours); emits wasm-EH unwinding by default under `panic=unwind` (Rust ≥ 1.93), and stamps LLVM `target_features` metadata newer binaryen tools understand but older ones reject |
| .NET SDK | 10.0.1xx (`rollForward: latestFeature`) + wasm-tools workload (manifest 10.0.105) | `global.json`; workload audited by `tools/lib/toolchains.py` | Owns the final emcc link of `dotnet.native.wasm`; `WasmEnableExceptionHandling=true` (pinned in `src/WasmHost.props`) must agree with the Rust EH mode |
| Emscripten | 3.1.56 (the workload's bundled pack) | Comes with the workload; drift tripwire in `toolchains.py` (`_EMSCRIPTEN_PIN`) | Its wasm-ld does the link; its binaryen (wasm-opt) is *older* than rustc's LLVM — see the symbol-map exclusion below |
| Node | v20 (PATH) | Not formally pinned yet (M3 CI will) | The wasm-desktop host engine. Its V8 supports the legacy wasm-EH flavor but not exnref; a rustc bump that changes the emitted EH *flavor* can break the Node host while the browser (current Chrome supports both) still works |

**What landed at M2: probe rung A** — prebuilt std, `panic=unwind`, default wasm-EH, .NET defaults. `catch_unwind` panic containment **works on wasm**, on both hosts; the M1 FFI rails hold everywhere. Rungs B (`-Cpanic=abort`) and C (nightly `-Zbuild-std` against the workload emsdk) were prepared but never needed. No `EmccExtraLDFlags` are in use: the link produced no duplicate-symbol or setjmp/longjmp failures.

## Failures actually hit at M2, and their fixes

These are the drift symptoms this specific matched set produced; treat them as the glossary's first entries.

1. **`wasm-opt: Unknown option '--enable-bulk-memory-opt'`** during the emcc link step. Cause: rustc 1.96's LLVM stamps `target_features` (`bulk-memory-opt`, `call-indirect-overlong`) that emscripten 3.1.56's older binaryen doesn't know; emcc forwards the module's feature list to any wasm-opt pass it runs. Hit twice, both fixed in `src/WasmHost.props`: (a) Debug builds — the only wasm-opt pass is symbol-map emission, which the plain-SDK (wasmconsole) path turns on by default → `WasmEmitSymbolMap=false`; (b) `dotnet publish -c Release` — the link defaults to `-O2`, whose wasm-opt optimization pass hits the same wall → `EmccLinkOptimizationFlag=-O1` (skips wasm-opt entirely; the optimization loss is the accepted cost). **The real fix for both is closing the LLVM↔binaryen version gap** (workload bump, or rung C); until then any newly-appearing wasm-opt pass (AOT, future SDK changes) will fail the same way.
2. **Runtime error `Error: buckminster_core` (a rejected wasm import).** Cause: a `DllImport` module name only resolves to statically-linked code when it matches a linked native file's *name*, and the `lib` prefix is **not** stripped in that match; an unmatched name silently becomes an external JS import that fails at instantiation/call time. Fix: `build-rust-wasm` copies cargo's `libbuckminster_core.a` to `buckminster_core.a` and `WasmHost.props` links that. Never fork the binding name per target.
3. **Abort in `mono_wasm_get_interp_to_native_trampoline` (aot-runtime-wasm.c) before the first FFI call.** Cause: mono's wasm interp-to-native path cannot map **function-pointer parameter types** in pinvoke signatures (`type_to_c` aborts on them — the dotnet/runtime #56145 class); the interp hits this while transforming any method that *contains* such a call, even if a different call runs first. Fix: `NativeMethods.buck_callback_invoke` takes the callback as `IntPtr` (ABI-identical; call sites cast `(IntPtr)(delegate* unmanaged<...>)&Callback`). This is a standing constraint on all future bindings: **no function-pointer types in pinvoke signatures — IntPtr at the boundary.**

## Deployment shape: static files, no server component

The browser host has no server side. `WasmAppHost` (what `dotnet run` launches) is a dev-time convenience; the deployable artifact is `dotnet publish -c Release` → `bin/Release/net10.0/publish/wwwroot/` — plain static files (`index.html`, `main.<fingerprint>.js`, `_framework/` with dotnet.js + the wasm + the assemblies), verified served by a bare `python3 -m http.server` with the page running correctly in Chrome. ~12 MB uncompressed at M2; the publish also emits precompressed `.br`/`.gz` siblings for every asset, which a dumb server ignores (it just serves the uncompressed files — fine) and a content-negotiating static host uses for real-world sizes. Single-threaded wasm needs no COOP/COEP headers; the only server requirement worth knowing is a correct `application/wasm` MIME type, which any non-ancient server (including Python's) has. A proper `publish`/`deploy` tool command is deliberately deferred until deployment is a real workflow.

## Where the workload emsdk lives

`$DOTNET_ROOT/packs/Microsoft.NET.Runtime.Emscripten.<version>.Sdk.<rid>/<packver>/tools/emscripten` — on this machine: `/usr/share/dotnet/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64/10.0.5/tools/emscripten` (with sibling `.Node` and `.Cache` packs). Enumerate with `ls $DOTNET_ROOT/packs | grep Emscripten` or `dotnet workload list` for the manifest version.

**cargo does not need emcc for the staticlib** — an archive has no link step, which is why `build-rust-wasm` works with no emsdk on PATH. The moment cargo must link a wasm *executable* (M3: `cargo test --target wasm32-unknown-emscripten`), cargo becomes a second emsdk consumer and must resolve **the workload's** emsdk, not a stray `~/emsdk`: prepend the pack's `tools/emscripten` dir to PATH for those invocations (wired into the tooling at M3, not before).

Also deferred to M3: the M0 policy says workloads are auto-provisioned, but M2 only *audits* wasm-tools (`ensure_dotnet_wasm`) — a system-owned SDK needs sudo for workload installs and .NET 10 workload-set scripting has footguns; the M3 CI fresh-machine story is the right time to close that gap.

## Template channel

The host projects were hand-written against the shapes generated by `dotnet new wasmconsole` / `dotnet new wasmbrowser` from `Microsoft.NET.Runtime.WebAssembly.Templates.net10` (the `.net10` suffix matters; the un-suffixed package is stuck at 8.0.x). `wasmconsole` is experimental (PLAN.md risk): if the template dies, the checked-in csproj keeps working (it's sugar over the wasm-tools runtime pack + WasmAppBuilder targets); worst case is a hand-written Node host over the same publish output. WASI is dead in .NET 10 — `wasiconsole` generates a non-functional app; the Node-hosted `browser-wasm` build *is* the console story.

## Re-verification checklist (run on ANY bump of any component)

1. `./tool.bat build` — the wasm hosts' emcc link is part of the default build; a set mismatch should fail here, at walking-skeleton size.
2. `dotnet run` in `src/host-wasmnode/cs` — all FfiSmoke lines correct in the terminal, **including panic containment** (it pins the EH mode agreement).
3. `dotnet run` in `src/host-web/cs` — same lines rendered in the page (headless: `google-chrome-stable --headless=new --virtual-time-budget=15000 --dump-dom <url>`); a failure renders in red on the page, not just the console.
4. If anything fails, diagnose against the glossary below, and record any new failure class here.
5. On a rustc bump specifically: re-check the Node host separately from the browser (EH-flavor divergence hits Node first), and try removing any `EmccExtraLDFlags` workarounds that may have accreted (none yet) — they mask exactly the class of error a bump can introduce.

## Failure-class glossary

| Symptom | Meaning | Knob |
|---|---|---|
| `wasm-opt: Unknown option '--enable-<feature>'` | rustc's LLVM is newer than the workload's binaryen | Avoid the wasm-opt pass (e.g. `WasmEmitSymbolMap=false`), or close the version gap (workload bump / rung C) |
| `wasm-ld: duplicate symbol` | Rust `compiler-builtins` colliding with emscripten's compiler-rt | Capture the verbatim list, then `EmccExtraLDFlags -Wl,--allow-multiple-definition`; revisit on every bump |
| Undefined libc/sysroot symbols at link | Prebuilt-std ↔ emscripten sysroot drift | Rung C (`-Zbuild-std` against the workload emsdk) |
| Missing setjmp/longjmp support at link or trap in panic path | EH/SjLj mode mismatch | `-sSUPPORT_LONGJMP=wasm` via `EmccExtraLDFlags`; check `WasmEnableExceptionHandling` agreement |
| Runtime `Error: <library name>` from dotnet.runtime.js | DllImport module name didn't match any linked file | Match the archive filename to the DllImport name (see fix 2) |
| Abort in `mono_wasm_get_interp_to_native_trampoline` | Unmappable parameter type in a pinvoke signature (fn pointers) | IntPtr at the boundary (see fix 3) |
| Fails on Node, works in browser | Engine wasm-EH *flavor* support (exnref vs legacy), not sysroot drift | Check Node version / V8 features before blaming the link |
| Signature-mismatch trap at call time | Rust export and C# import disagree on ABI | Fix the binding; the layout-assertion tests (M4) narrow struct cases |
