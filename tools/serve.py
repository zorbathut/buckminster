"""`./tool.bat serve` -- build, then serve the browser host's dev server until Ctrl+C (dotnet run prints the URL to open)."""

import os
import sys

from lib import toolchains
from lib import util


def run(args: list[str]) -> None:
    if args:
        print(f"serve takes no arguments (got: {' '.join(args)})")
        sys.exit(1)
    tc = toolchains.ensure_toolchains()
    env = dict(os.environ)
    env["BUCK_CARGO"] = tc.cargo
    env["BUCK_DOTNET"] = tc.dotnet
    # Build first so serving from a clean tree just works; dotnet run's own incremental build then has nothing to do.
    util.run(["scons"], cwd=util.repo_root(), env=env)
    # check=False + KeyboardInterrupt: Ctrl+C is the normal way to stop a dev server, not a build failure to report.
    try:
        util.run([tc.dotnet, "run"], check=False, cwd=os.path.join(util.repo_root(), "src", "host-web", "cs"))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    run(sys.argv[1:])
