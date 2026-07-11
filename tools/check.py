"""`./tool.bat check` -- static gates: pyright strict over the build tooling (config: [tool.pyright] in tools/lib/pyproject.toml), rustfmt, and clippy. CI-enforced."""

import os
import sys

from lib import toolchains
from lib import util


def run(args: list[str]) -> None:
    if args:
        print(f"check takes no arguments (got: {' '.join(args)})")
        sys.exit(1)
    # The pyright config lives in tools/lib/pyproject.toml ([tool.pyright]). -I: build.py/test.py shadow packages other tooling imports (same hazard bootstrap.py documents for poetry). First run downloads a pinned node runtime user-local (the pip pyright package's mechanism) -- consistent with the provisioning policy.
    util.run([sys.executable, "-I", "-m", "pyright", "--project", os.path.join(util.repo_root(), "tools", "lib")])
    # cargo through the pin (never PATH), cwd src/ for rust-toolchain.toml discovery -- same rules as every cargo invocation in this tree.
    cargo = toolchains.ensure_cargo()
    src = os.path.join(util.repo_root(), "src")
    util.run([cargo, "fmt", "--check"], cwd=src)
    util.run([cargo, "clippy", "--workspace", "--all-targets", "--", "-D", "warnings"], cwd=src)


if __name__ == "__main__":
    run(sys.argv[1:])
