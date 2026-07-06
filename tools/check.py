"""`./tool.bat check` -- pyright strict over the build tooling (config: [tool.pyright] in tools/lib/pyproject.toml). CI-enforced from M3 on."""

import os
import sys

from lib import util


def run(args: list[str]) -> None:
    # The pyright config lives in tools/lib/pyproject.toml ([tool.pyright]). -I: build.py/test.py shadow packages other tooling imports (same hazard bootstrap.py documents for poetry). First run downloads a pinned node runtime user-local (the pip pyright package's mechanism) -- consistent with the provisioning policy.
    util.run([sys.executable, "-I", "-m", "pyright", "--project", os.path.join(util.repo_root(), "tools", "lib")] + args)


if __name__ == "__main__":
    run(sys.argv[1:])
