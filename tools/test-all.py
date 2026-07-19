"""`./tool.bat test-all` -- the full test matrix: both languages on all three run targets (linux native, wasm-desktop/Node, web/browser), with a per-target summary. Every section runs even after a failure; the exit code is nonzero if anything failed. `./tool.bat test` remains the quick native slice."""

import os
import shutil
import subprocess
import sys
from typing import cast

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


# The M4 done-when demo, end to end as shipped: the built desktop host runs the demo usergame to 100 ticks and exits clean. Runs the dll scons already built (dotnet run would rebuild outside scons's rust-before-dotnet ordering). Exit 0 alone is not a pass: the sentinel line proves the demo actually ran.
def _run_desktop_smoke(dotnet: str, src: str) -> bool:
    command = [dotnet, os.path.join(src, "host-desktop", "cs", "bin", "Debug", "net10.0", "Buckminster.Host.Desktop.dll"), "--headless"]
    print("Executing: " + " ".join(command))
    ok = True
    try:
        completed = subprocess.run(command, cwd=util.repo_root(), capture_output=True, text=True, timeout=120)
        print(completed.stdout, end="")
        if completed.stderr:
            print("--- host stderr ---")
            print(completed.stderr, end="")
        if completed.returncode != 0:
            print(f"FAIL: the desktop host exited with code {completed.returncode}")
            ok = False
        if "BUCK-DEMO-EXIT ticks=100" not in completed.stdout.splitlines():
            print("FAIL: the desktop host never printed the exact 'BUCK-DEMO-EXIT ticks=100' sentinel line; exit 0 without the demo provably running is not a pass")
            ok = False
    except subprocess.TimeoutExpired as error:
        # The exception carries whatever the host printed before hanging; swallowing it would make a hang undiagnosable.
        for stream_name, partial in (("stdout", error.stdout), ("stderr", error.stderr)):
            # The runtime type here is platform-and-path-dependent even with text=True (CPython's POSIX partial-output path joins raw bytes and never decodes; Windows' post-kill re-communicate yields str), so neither typeshed's bytes|None nor an assumption can be trusted; cast to object (an annotation doesn't defeat pyright's narrowing, a cast does) and handle both.
            partial_widened = cast(object, partial)
            if partial_widened:
                print(f"--- partial {stream_name} before timeout ---")
                print(partial_widened.decode(errors="replace") if isinstance(partial_widened, bytes) else partial_widened)
        print("FAIL: the desktop host timed out after 120s; the demo usergame never queued exit (partial output above)")
        ok = False
    return ok


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
    # Separate invocations, deliberately: plain `cargo test` covers the default-members (which exclude buckminster-ffi-dump so its ffi-dump feature can't unify into shipped artifacts, and the native-only buckminster-host-desktop -- src/Cargo.toml); the explicit -p runs cover those two in their own resolution universes.
    rust_native_ok = _run_section([tc.cargo, "test"], cwd=src)
    rust_native_ok = _run_section([tc.cargo, "test", "-p", "buckminster-ffi-dump"], cwd=src) and rust_native_ok
    rust_native_ok = _run_section([tc.cargo, "test", "-p", "buckminster-host-desktop"], cwd=src) and rust_native_ok
    results.append(("rust-native", rust_native_ok))

    _banner("rust-wasm-node")
    # The node cells get timeouts because a hung wasm runtime otherwise stalls the matrix (and CI) forever; the browser cells are already bounded by the CDP driver's own timeout. Default-members only: the dump bin is host-native tooling and never builds for wasm.
    results.append(("rust-wasm-node", _run_section([tc.cargo, "test", "--target", "wasm32-unknown-emscripten"], cwd=src, env=emsdk, timeout=600)))

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

    _banner("cs-desktop-host")
    results.append(("cs-desktop-host", _run_desktop_smoke(tc.dotnet, src)))

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
