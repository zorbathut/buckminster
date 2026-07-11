"""SCons target definitions. Invoked from the SConstruct shim at the repo root; all build logic lives here, typed. cargo and MSBuild remain the fine-grained inner build systems -- SCons owns the coarse meta-DAG (trivial for now; shader pipeline, wasm link assembly, etc. land here in later milestones)."""

import os
import shutil
import subprocess

from lib import sconsfacade
from lib import util


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


def _build_dotnet() -> int:
    return _run_in(util.repo_root(), [_tool_env("BUCK_DOTNET"), "build", "Buckminster.slnx"])


def define() -> None:
    rust = sconsfacade.phony("build-rust", _build_rust)
    rust_wasm = sconsfacade.phony("build-rust-wasm", _build_rust_wasm)
    dotnet = sconsfacade.phony("build-dotnet", _build_dotnet)
    # dotnet copies the cargo-built native library into its output dirs and links the wasm staticlib into the wasm hosts, so both rust builds must run first (alias-member order is not an ordering contract, especially under -j).
    sconsfacade.depends(dotnet, rust)
    sconsfacade.depends(dotnet, rust_wasm)
    sconsfacade.default(sconsfacade.group("build", [rust, rust_wasm, dotnet]))
