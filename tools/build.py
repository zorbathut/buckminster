"""`./tool.bat build` -- audit/provision toolchains, then run the SCons build. Extra args pass through to scons (e.g. `./tool.bat build build-rust`)."""

import os
import sys

from lib import toolchains
from lib import util


def run(args: list[str]) -> None:
    tc = toolchains.ensure_toolchains()
    env = dict(os.environ)
    env["BUCK_CARGO"] = tc.cargo
    env["BUCK_DOTNET"] = tc.dotnet
    util.run(["scons"] + args, cwd=util.repo_root(), env=env)


if __name__ == "__main__":
    run(sys.argv[1:])
