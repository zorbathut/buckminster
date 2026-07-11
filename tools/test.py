"""`./tool.bat test` -- build, then run the Rust and C# test suites natively: the quick slice. The full multi-target matrix is `./tool.bat test-all`."""

import os
import sys

from lib import toolchains
from lib import util


def run(args: list[str]) -> None:
    if args:
        # cargo test and dotnet test share no argument vocabulary, so a blind pass-through to both can only break one of them. Per-suite selection is the M3 matrix command's job.
        print(f"test takes no arguments (got: {' '.join(args)})")
        sys.exit(1)
    tc = toolchains.ensure_toolchains()
    env = dict(os.environ)
    env["BUCK_CARGO"] = tc.cargo
    env["BUCK_DOTNET"] = tc.dotnet
    util.run(["scons"], cwd=util.repo_root(), env=env)
    # cwd src/: cargo workspace + rust-toolchain.toml discovery, same as the build target.
    util.run([tc.cargo, "test", "--workspace"], cwd=os.path.join(util.repo_root(), "src"))
    # Microsoft.Testing.Platform mode (opted into via global.json "test.runner" -- the .NET 10 SDK offers no per-project alternative); its CLI takes --solution, not a bare path. --no-build: scons just built this exact configuration, and rebuilding would also re-run the wasm hosts' emcc link.
    util.run([tc.dotnet, "test", "--solution", "Buckminster.slnx", "--no-build"], cwd=util.repo_root())


if __name__ == "__main__":
    run(sys.argv[1:])
