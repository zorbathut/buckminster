"""`./tool.bat test-all` -- the full test matrix: both languages on all three run targets (linux native, wasm-desktop/Node, web/browser), with a per-target summary. Every section runs even after a failure; the exit code is nonzero if anything failed. `./tool.bat test` remains the quick native slice."""

import os
import shutil
import subprocess
import sys

from lib import toolchains
from lib import util
from lib import wasmbrowser


def _banner(name: str) -> None:
    print()
    print(f"======== {name} ========")


def _run_section(command: list[str], cwd: str, env: dict[str, str] | None = None) -> bool:
    print("Executing: " + " ".join(command))
    return subprocess.run(command, cwd=cwd, env=env).returncode == 0


def _run_browser_page(chrome: str | None, serve_dir: str, page: str, label: str) -> bool:
    if chrome is None:
        print(f"FAIL: {wasmbrowser.chrome_missing_message()}")
        return False
    try:
        code, text = wasmbrowser.run_page(chrome, serve_dir, page)
    except (RuntimeError, OSError) as error:
        # TimeoutError is an OSError; either way the section fails and the summary still prints.
        print(f"FAIL: {label}: {error}")
        return False
    print(text)
    if code is None:
        print(f"FAIL: {label}: the page never produced the BUCK-TEST-EXIT sentinel (see captured output above)")
        return False
    return code == 0


def run(args: list[str]) -> None:
    if args:
        print(f"test-all takes no arguments (got: {' '.join(args)})")
        sys.exit(1)
    tc = toolchains.ensure_toolchains()
    src = os.path.join(util.repo_root(), "src")
    env = dict(os.environ)
    env["BUCK_CARGO"] = tc.cargo
    env["BUCK_DOTNET"] = tc.dotnet
    util.run(["scons"], cwd=util.repo_root(), env=env)
    emsdk = toolchains.emsdk_env(tc.dotnet)
    chrome = wasmbrowser.find_chrome()

    results: list[tuple[str, bool]] = []

    _banner("rust-native")
    results.append(("rust-native", _run_section([tc.cargo, "test", "--workspace"], cwd=src)))

    _banner("rust-wasm-node")
    results.append(("rust-wasm-node", _run_section([tc.cargo, "test", "--workspace", "--target", "wasm32-unknown-emscripten"], cwd=src, env=emsdk)))

    _banner("rust-wasm-browser")
    ok = True
    try:
        # Staging dirs are keyed on hash-suffixed binary names; clear the parent so stale ones don't accumulate.
        shutil.rmtree(os.path.join(src, "target", "browser-rust-tests"), ignore_errors=True)
        binaries = wasmbrowser.list_rust_wasm_test_binaries(tc.cargo, emsdk)
        if not binaries:
            print("FAIL: no wasm test binaries found")
            ok = False
        for binary in binaries:
            staging = wasmbrowser.stage_rust_test(binary)
            ok = _run_browser_page(chrome, staging, "index.html", os.path.basename(binary)) and ok
    except (RuntimeError, OSError) as error:
        print(f"FAIL: {error}")
        ok = False
    results.append(("rust-wasm-browser", ok))

    _banner("cs-native")
    results.append(("cs-native", _run_section([tc.dotnet, "test", "--solution", "Buckminster.slnx", "--no-build"], cwd=util.repo_root())))

    _banner("cs-wasm-node")
    bundle = os.path.join(src, "host-wasmnode", "cs", "bin", "Debug", "net10.0", "browser-wasm", "AppBundle")
    results.append(("cs-wasm-node", _run_section(["node", "main.mjs"], cwd=bundle)))

    _banner("cs-wasm-browser")
    # Publish is what produces static-servable files for the driver; untrimmed per WasmHost.props, and the emcc relink here is the matrix's long pole.
    published = _run_section([tc.dotnet, "publish", os.path.join(src, "host-web", "cs", "Buckminster.Host.Web.csproj"), "-c", "Debug"], cwd=util.repo_root())
    wwwroot = os.path.join(src, "host-web", "cs", "bin", "Debug", "net10.0", "publish", "wwwroot")
    results.append(("cs-wasm-browser", published and _run_browser_page(chrome, wwwroot, "index.html", "host-web test page")))

    _banner("summary")
    failed = False
    for name, passed in results:
        print(f"{name:20s} {'PASS' if passed else 'FAIL'}")
        failed = failed or not passed
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    run(sys.argv[1:])
