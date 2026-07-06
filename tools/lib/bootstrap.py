"""Tool-environment bootstrap: verifies and self-heals the tools venv, then dispatches to a command script. Runs under the system python; everything past this file runs inside the poetry-managed venv. Adapted from planefarer's tools/bootstrap.py, same owner."""

import os
import sys

# Gate before importing anything of ours: lib modules use 3.10+ syntax that throws a bewildering TypeError at import time on older interpreters, and "install a newer Python" beats a traceback. (X | Y annotations parse fine on old pythons; they only explode when evaluated, so this check is reachable.)
if sys.version_info < (3, 10):
    print("--------")
    print(f"Error: Python 3.10+ is required; this is {sys.version.split()[0]}.")
    print("Please install a newer Python from https://www.python.org/downloads/ or your package manager.")
    sys.exit(1)

# Running as a script puts tools/lib/ on sys.path, but the project-wide import convention is `from lib import x` (it's what command scripts and pyright resolve), so add tools/ too. append, not insert(0): commands in tools/ shadow package names others use (build.py vs PyPA build).
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import shutil
import subprocess

from lib import util


def available_commands() -> list[str]:
    # Every tools/*.py is a command; support code lives in tools/lib/.
    return sorted(name[:-3] for name in os.listdir("tools") if name.endswith(".py"))


def print_usage() -> None:
    print("Usage: ./tool.bat <command> [args...]")
    print("Commands: " + ", ".join(available_commands()))


def verify_venv(venv_python: str) -> bool:
    """Check that the venv python runs and poetry works inside it."""
    if not os.path.exists(venv_python):
        return False
    try:
        result = subprocess.run([venv_python, "--version"], capture_output=True, timeout=10)
        if result.returncode != 0:
            return False
        result = subprocess.run([venv_python, "-I", "-m", "poetry", "--version"], capture_output=True, timeout=30)
        return result.returncode == 0
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return False


def execute(token: str | None, args: list[str]) -> None:
    # bootstrap runs under the system python without -u, so when stdout is a pipe our prints get block-buffered while child processes write straight through -- the output arrives wildly out of order. Line-buffer to keep the interleaving honest. (Dispatched scripts run under `python -u` and don't need this.)
    sys.stdout.reconfigure(line_buffering=True)  # type: ignore[union-attr]

    # Everything below uses paths relative to the repo root; bootstrap.py lives in tools/lib/, so the root is three levels up. Keyed off __file__ so invocation cwd doesn't matter.
    os.chdir(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

    if token is None or token not in available_commands():
        if token is not None:
            print(f"Unknown command: {token}")
        print_usage()
        sys.exit(1)

    venv_path = os.path.abspath(os.path.join("tools", "lib", ".venv"))
    venv_python = util.platformswitch(
        linux=os.path.join(venv_path, "bin", "python"),
        windows=os.path.join(venv_path, "Scripts", "python.exe"),
        mac=os.path.join(venv_path, "bin", "python"),
    )

    if os.path.exists(venv_path) and not verify_venv(venv_python):
        print("Tools venv appears corrupted, rebuilding...")
        shutil.rmtree(venv_path)

    if not os.path.exists(venv_path):
        print("Creating tools venv...")
        util.run([sys.executable, "-m", "venv", venv_path])
        print("Installing poetry into the tools venv...")
        util.run([venv_python, "-I", "-m", "pip", "install", "--quiet", "poetry"])

    # Poetry runs under -I (isolated mode): without isolation python puts the cwd on sys.path, where our modules can shadow packages poetry imports (the command scripts shadow PyPA `build` and stdlib `test`). The dispatched script below deliberately keeps the normal path -- it needs `from lib import ...` to resolve.
    # tools/lib/poetry.toml sets virtualenvs.in-project, so this installs into the same tools/lib/.venv poetry itself lives in (which is also where pyright looks for imports, per [tool.pyright] in tools/lib/pyproject.toml). If Windows ever hits python-poetry issue #10219 here, planefarer's workaround was `poetry config virtualenvs.use-poetry-python true --local`.
    util.run([venv_python, "-I", "-m", "poetry", "install", "--no-root", "--quiet"], cwd=os.path.join("tools", "lib"))

    # cwd is tools/lib (where poetry's pyproject lives); the command script is one level up. python puts the *script's* directory (tools/) on sys.path, which is what makes the commands' `from lib import ...` work. -u: unbuffered, so child output interleaves correctly under CI log capture.
    util.run([venv_python, "-I", "-m", "poetry", "run", "python", "-u", os.path.join("..", f"{token}.py")] + args, cwd=os.path.join("tools", "lib"))


if __name__ == "__main__":
    execute(sys.argv[1] if len(sys.argv) > 1 else None, sys.argv[2:])
