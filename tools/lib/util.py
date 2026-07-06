"""Shared helpers for tools/ scripts. Adapted (trimmed) from planefarer's tools/util.py, same owner."""

import os
import subprocess
import sys
from typing import TypeVar

T = TypeVar("T")


def repo_root() -> str:
    # This file lives in tools/lib/, so the root is three levels up.
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def platformswitch(linux: T, windows: T, mac: T) -> T:
    if sys.platform.startswith("linux"):
        return linux
    elif sys.platform.startswith("win32"):
        return windows
    elif sys.platform.startswith("darwin"):
        return mac
    else:
        raise RuntimeError(f"Unidentified OS: {sys.platform}")


def run(command: list[str], check: bool = True, cwd: str | None = None, env: dict[str, str] | None = None) -> int:
    """Run a command, echoing it first. With check (the default), a failure prints the exit code and exits the process with it -- tool scripts have nothing useful to do past a failed step, and the failing command has already printed its own error."""
    print("Executing: " + " ".join(command))
    result = subprocess.run(command, cwd=cwd, env=env)
    if check and result.returncode != 0:
        print(f"Command failed with exit code {result.returncode}: " + " ".join(command))
        sys.exit(result.returncode)
    return result.returncode
