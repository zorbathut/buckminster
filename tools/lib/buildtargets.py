"""SCons target definitions. Invoked from the SConstruct shim at the repo root; all build logic lives here, typed. cargo and MSBuild remain the fine-grained inner build systems -- SCons owns the coarse meta-DAG (trivial for now; shader pipeline, wasm link assembly, etc. land here in later milestones)."""

import os
import shutil
import subprocess

from lib import sconsfacade
from lib import util
from lib import wasmnative


def _tool_env(var: str) -> str:
    value = os.environ.get(var)
    if value is None:
        # No fallback to bare `cargo`/`dotnet` from PATH: that would silently bypass the rustup/global.json pins. The tool entry point is what resolves them.
        raise RuntimeError(f"{var} is not set -- run builds via ./tool.bat build, which resolves and pins the toolchains")
    return value


def _run_in(cwd: str, command: list[str]) -> int:
    print("Executing: " + " ".join(command))
    return subprocess.run(command, cwd=cwd).returncode


def _build_rust() -> int:
    # cwd src/: that's where the cargo workspace, Cargo.lock, and rust-toolchain.toml live (walk-up discovery for all three starts at the cwd).
    return _run_in(os.path.join(util.repo_root(), "src"), [_tool_env("BUCK_CARGO"), "build", "--workspace"])


def _build_rust_wasm() -> int:
    # cargo rustc with a crate-type override: the staticlib is per-invocation because listing it in Cargo.toml would make wasm builds also attempt the cdylib link (which needs emcc at cargo time; an archive needs no linker at all). The wasm hosts link this archive via NativeFileReference (src/WasmHost.props).
    code = _run_in(os.path.join(util.repo_root(), "src"), [_tool_env("BUCK_CARGO"), "rustc", "-p", "buckminster-core", "--target", "wasm32-unknown-emscripten", "--crate-type", "staticlib"])
    if code != 0:
        return code
    # Copy to the DllImport-matching name: "buckminster_core" only resolves to statically-linked code when it matches a linked file's name, and the lib prefix is not stripped in that match (docs/wasm-toolchain.md).
    out_dir = os.path.join(util.repo_root(), "src", "target", "wasm32-unknown-emscripten", "debug")
    shutil.copy2(os.path.join(out_dir, "libbuckminster_core.a"), os.path.join(out_dir, "buckminster_core.a"))
    return 0


_WASM_HOST_DIRS = [os.path.join("src", "host-wasmnode", "cs"), os.path.join("src", "host-web", "cs")]


# The m2n staleness check, self-healing (docs/wasm-toolchain.md glossary; module doc in wasmnative.py). for-build pairs ONLY: `dotnet build` cannot refresh a for-publish binary, so checking those here would false-alarm; the publish step (test-all's browser cell) owns them.
def _check_and_heal_m2n() -> int:
    hosts = [os.path.join(util.repo_root(), host) for host in _WASM_HOST_DIRS]
    for attempt in range(2):
        pairs = [pair for host in hosts for pair in wasmnative.find_checkable_pairs(host, "build")]
        if not pairs:
            # Zero checkable pairs means the paths drifted (TFM bump, config change), not that everything is fine -- silent-green is the exact rot this check exists to prevent. host-wasmnode's for-build pair exists after every build today.
            print("Error: the m2n staleness check found no checkable for-build pairs under either wasm host; the obj layout has drifted and the check needs updating (tools/lib/wasmnative.py)")
            return 1
        stale = [(pair, missing) for pair in pairs if (missing := wasmnative.find_stale_cookies(pair))]
        if not stale:
            return 0
        for pair, missing in stale:
            print(f"m2n staleness detected -- {wasmnative.describe_stale(pair, missing)}")
        if attempt == 1:
            print("Error: m2n staleness survived a forced full relink; this is not incremental staleness, it is a real bug")
            return 1
        wasmnative.delete_wasm_obj_dirs([host for host in hosts if any(pair.startswith(host) for pair, _ in stale)])
        code = _run_in(util.repo_root(), [_tool_env("BUCK_DOTNET"), "build", "Buckminster.slnx"])
        if code != 0:
            return code
    return 1


def _build_dotnet() -> int:
    code = _run_in(util.repo_root(), [_tool_env("BUCK_DOTNET"), "build", "Buckminster.slnx"])
    if code != 0:
        return code
    return _check_and_heal_m2n()


def define() -> None:
    rust = sconsfacade.phony("build-rust", _build_rust)
    rust_wasm = sconsfacade.phony("build-rust-wasm", _build_rust_wasm)
    dotnet = sconsfacade.phony("build-dotnet", _build_dotnet)
    # dotnet copies the cargo-built native library into its output dirs and links the wasm staticlib into the wasm hosts, so both rust builds must run first (alias-member order is not an ordering contract, especially under -j).
    sconsfacade.depends(dotnet, rust)
    sconsfacade.depends(dotnet, rust_wasm)
    sconsfacade.default(sconsfacade.group("build", [rust, rust_wasm, dotnet]))
