"""`./tool.bat test-all` -- the full test matrix: both languages on all three run targets (linux native, wasm-desktop/Node, web/browser), with a per-target summary. Every section runs even after a failure; the exit code is nonzero if anything failed. `./tool.bat test` remains the quick native slice."""

import os
import shutil
import subprocess
import sys

from lib import toolchains
from lib import util
from lib import wasmbrowser
from lib import wasmnative


def _banner(name: str) -> None:
    print()
    print(f"======== {name} ========")


def _run_section(command: list[str], cwd: str, env: dict[str, str] | None = None, timeout: float | None = None) -> bool:
    print("Executing: " + " ".join(command))
    try:
        return subprocess.run(command, cwd=cwd, env=env, timeout=timeout).returncode == 0
    except subprocess.TimeoutExpired:
        print(f"FAIL: timed out after {timeout}s (if this is the C# cell, the usual suspect is a genuinely-async test deadlocking under RunOnMainThread -- see RunnerWasm.cs)")
        return False


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
    # The node cells get timeouts because a hung wasm runtime otherwise stalls the matrix (and CI) forever; the browser cells are already bounded by the CDP driver's own timeout.
    results.append(("rust-wasm-node", _run_section([tc.cargo, "test", "--workspace", "--target", "wasm32-unknown-emscripten"], cwd=src, env=emsdk, timeout=600)))

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
    results.append(("cs-wasm-node", _run_section(["node", "main.mjs"], cwd=bundle, timeout=600)))

    _banner("cs-wasm-browser")
    # Publish is what produces static-servable files for the driver; untrimmed per WasmHost.props, and the emcc relink here is the matrix's long pole. The publish owns the for-publish m2n pairs, so the staleness check-and-heal for them lives here (host-web only relinks at publish, making this its only m2n coverage; see tools/lib/wasmnative.py).
    host_web = os.path.join(src, "host-web", "cs")
    publish_command = [tc.dotnet, "publish", os.path.join(host_web, "Buckminster.Host.Web.csproj"), "-c", "Debug"]
    published = _run_section(publish_command, cwd=util.repo_root())
    if published:
        pairs = wasmnative.find_checkable_pairs(host_web, "publish")
        if not pairs:
            # Same floor as the build-side check: zero checkable pairs after a successful publish means the obj layout drifted, and silent-green is the exact rot the check exists to prevent. This is host-web's ONLY m2n coverage.
            print("FAIL: the m2n staleness check found no checkable for-publish pairs after a successful publish; the obj layout has drifted (tools/lib/wasmnative.py)")
            published = False
        stale_pairs = [(pair, missing) for pair in pairs if (missing := wasmnative.find_stale_cookies(pair))]
        if published and stale_pairs:
            for pair, missing in stale_pairs:
                print(f"m2n staleness detected -- {wasmnative.describe_stale(pair, missing)}")
            wasmnative.delete_wasm_obj_dirs([host_web])
            published = _run_section(publish_command, cwd=util.repo_root())
            if published:
                pairs = wasmnative.find_checkable_pairs(host_web, "publish")
                if not pairs or any(wasmnative.find_stale_cookies(pair) for pair in pairs):
                    print("FAIL: m2n staleness survived a forced full re-publish; this is a real bug, not incremental staleness")
                    published = False
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
