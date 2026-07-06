"""SCons target definitions. Invoked from the SConstruct shim at the repo root; all build logic lives here, typed. cargo and MSBuild remain the fine-grained inner build systems -- SCons owns the coarse meta-DAG (trivial for now; shader pipeline, wasm link assembly, etc. land here in later milestones)."""

import os
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


def _build_dotnet() -> int:
    return _run_in(util.repo_root(), [_tool_env("BUCK_DOTNET"), "build", "Buckminster.slnx"])


def define() -> None:
    rust = sconsfacade.phony("build-rust", _build_rust)
    dotnet = sconsfacade.phony("build-dotnet", _build_dotnet)
    sconsfacade.default(sconsfacade.group("build", [rust, dotnet]))
