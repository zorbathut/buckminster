"""Toolchain audit and user-local auto-provisioning. Policy (PLAN.md): the install floor is git + Python + platform C++ build tools; everything else is either auto-provisioned user-local here (pinned, no admin rights) or audited with exact install instructions on failure."""

import dataclasses
import json
import os
import platform
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request

from lib import util


@dataclasses.dataclass
class Toolchains:
    cargo: str
    dotnet: str


def ensure_toolchains() -> Toolchains:
    ensure_cc()
    ensure_node()
    cargo = ensure_cargo()
    dotnet = ensure_dotnet()
    ensure_dotnet_wasm(dotnet)
    return Toolchains(cargo=cargo, dotnet=dotnet)


def _exe(name: str) -> str:
    return name + util.platformswitch(linux="", windows=".exe", mac="")


def _download(url: str, dest: str) -> None:
    print(f"Downloading {url}")
    with urllib.request.urlopen(url, timeout=300) as response, open(dest, "wb") as out:
        shutil.copyfileobj(response, out)


# --- C compiler / linker (audit-and-instruct; Rust needs a platform linker) ---


def ensure_cc() -> None:
    if util.platformswitch(linux=False, windows=True, mac=False):
        _ensure_msvc()
        return
    for compiler in ["gcc", "clang", "cc"]:
        if shutil.which(compiler) is not None:
            return
    print("--------")
    print("Error: C/C++ compiler not found (Rust needs it as a linker).")
    print("Please install a compiler:")
    print("  Ubuntu/Debian: sudo apt install build-essential")
    print("  Arch: sudo pacman -S base-devel")
    print("  macOS: xcode-select --install")
    sys.exit(1)


def _ensure_msvc() -> None:
    # vswhere check adapted from planefarer's tools/bootstrap.py, same owner.
    vswhere = os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe")
    if os.path.exists(vswhere):
        try:
            result = subprocess.run(
                [vswhere, "-products", "*", "-version", "[17.0,)", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath", "-format", "json"],
                capture_output=True, text=True, timeout=10)
            if result.returncode == 0:
                for install in json.loads(result.stdout):
                    if os.path.exists(os.path.join(install.get("installationPath", ""), "VC", "Auxiliary", "Build", "vcvars64.bat")):
                        return
        except (subprocess.TimeoutExpired, FileNotFoundError, json.JSONDecodeError):
            pass
    print("--------")
    print("Error: MSVC Build Tools not found (Rust's Windows linker).")
    print("")
    print("Install one of the following:")
    print("  Visual Studio Build Tools (free): https://visualstudio.microsoft.com/downloads/ under 'Tools for Visual Studio'")
    print("  Visual Studio Community (free): https://visualstudio.microsoft.com/downloads/")
    print("")
    print("Under Workloads, select 'Desktop development with C++' (make sure a Windows SDK is checked in Installation Details).")
    print("If you already have it installed, run the Visual Studio Installer, click Modify, and verify the above.")
    sys.exit(1)


# --- Rust (auto-provision rustup user-local; the pin lives in rust-toolchain.toml) ---


_RUSTUP_INIT_URLS = {
    ("linux", "x86_64"): "https://static.rust-lang.org/rustup/dist/x86_64-unknown-linux-gnu/rustup-init",
    ("linux", "aarch64"): "https://static.rust-lang.org/rustup/dist/aarch64-unknown-linux-gnu/rustup-init",
    ("windows", "AMD64"): "https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe",
    ("darwin", "x86_64"): "https://static.rust-lang.org/rustup/dist/x86_64-apple-darwin/rustup-init",
    ("darwin", "arm64"): "https://static.rust-lang.org/rustup/dist/aarch64-apple-darwin/rustup-init",
}


def _cargo_bin() -> str:
    # rustup-init honors CARGO_HOME, so we must too or a successful install looks broken afterwards.
    return os.path.join(os.environ.get("CARGO_HOME", os.path.join(os.path.expanduser("~"), ".cargo")), "bin")


def ensure_cargo() -> str:
    """Resolve cargo strictly through rustup -- a distro cargo silently ignores rust-toolchain.toml, which would make the toolchain pin decorative."""
    rustup = shutil.which("rustup")
    if rustup is None:
        candidate = os.path.join(_cargo_bin(), _exe("rustup"))
        if os.path.exists(candidate):
            rustup = candidate
    if rustup is None:
        _install_rustup()
        rustup = os.path.join(_cargo_bin(), _exe("rustup"))

    # Prefer the cargo proxy sitting next to rustup (covers distro rustup packages, whose proxies live outside ~/.cargo); fall back to the standard user-local location.
    for candidate in [os.path.join(os.path.dirname(rustup), _exe("cargo")), os.path.join(_cargo_bin(), _exe("cargo"))]:
        if os.path.exists(candidate):
            return candidate
    print("--------")
    print(f"Error: found rustup at {rustup} but no cargo proxy next to it or in {_cargo_bin()}.")
    print("The rustup installation looks broken; reinstall it from https://rustup.rs and re-run.")
    sys.exit(1)


def _install_rustup() -> None:
    key = (util.platformswitch(linux="linux", windows="windows", mac="darwin"), platform.machine())
    url = _RUSTUP_INIT_URLS.get(key)
    if url is None:
        print("--------")
        print(f"Error: no rustup-init download known for platform {key}.")
        print("Install rustup manually from https://rustup.rs and re-run.")
        sys.exit(1)
    print("rustup not found; installing it user-local (into ~/.cargo and ~/.rustup -- nothing system-wide is touched).")
    with tempfile.TemporaryDirectory() as tmp:
        installer = os.path.join(tmp, os.path.basename(url))
        _download(url, installer)
        os.chmod(installer, os.stat(installer).st_mode | stat.S_IXUSR)
        # No default toolchain: rustup auto-installs the rust-toolchain.toml pin (with its components/targets) on first cargo use, and any other toolchain would just be a second multi-hundred-MB download nothing uses. Minimal profile: rustc/cargo/std only.
        util.run([installer, "-y", "--no-modify-path", "--profile", "minimal", "--default-toolchain", "none"])


# --- Node (audit-only; PATH node is the wasm-desktop target's engine and the wasm test runner) ---


# The node major version the wasm targets are verified against -- the Node row of docs/wasm-toolchain.md's matched set. Warn-only tripwire: newer V8s change wasm-EH flavor support, the "fails on Node, works in browser" glossary class.
_NODE_PIN = 20


def ensure_node() -> None:
    node = shutil.which("node")
    if node is None:
        print("--------")
        print("Error: node not found on PATH (it hosts the wasm-desktop target and runs the wasm test suites).")
        print("Install Node 20 from https://nodejs.org/ or your package manager (Arch: sudo pacman -S nodejs; Ubuntu/Debian: sudo apt install nodejs).")
        sys.exit(1)
    try:
        result = subprocess.run([node, "--version"], capture_output=True, text=True, timeout=30)
        version = result.stdout.strip()
    except (subprocess.TimeoutExpired, OSError):
        version = ""
    if not version.startswith(f"v{_NODE_PIN}."):
        print(f"Warning: node is {version or 'unknown'}, verified pin is v{_NODE_PIN}.x; wasm-EH flavor support differs across V8 versions (docs/wasm-toolchain.md) -- run the re-verification checklist if wasm tests misbehave.")


# --- .NET wasm workload (auto-provisioned when the SDK root is user-writable, e.g. the bootstrap's own ~/.dotnet -- the M0 policy; a root-owned system SDK gets instructions instead, never auto-sudo) ---


# The emscripten the repo is verified against -- one component of docs/wasm-toolchain.md's matched set. Audit-time drift tripwire only; real verification is always link-and-run per that doc.
_EMSCRIPTEN_PIN = "3.1.56"


def ensure_dotnet_wasm(dotnet: str) -> None:
    """Audit that the wasm-tools workload is installed (it supplies the emscripten that links the Rust staticlib into the wasm hosts). Costs a ~1s `dotnet workload list` per tool invocation; acceptable until it isn't."""
    try:
        result = subprocess.run([dotnet, "workload", "list"], cwd=util.repo_root(), capture_output=True, text=True, timeout=120)
    except (subprocess.TimeoutExpired, OSError):
        result = None
    if result is None or result.returncode != 0:
        print("--------")
        print(f"Error: `{dotnet} workload list` failed; cannot verify the wasm-tools workload. Check the .NET SDK installation.")
        sys.exit(1)
    # Whole-token match: "wasm-tools-net8"/"-net9" are distinct down-level workload IDs and must not satisfy this check.
    if re.search(r"^wasm-tools(\s|$)", result.stdout, re.MULTILINE) is None:
        sdk_root = os.path.dirname(os.path.realpath(dotnet))
        if os.access(sdk_root, os.W_OK):
            print("wasm-tools workload not installed; installing it into the user-writable SDK (no admin rights needed).")
            util.run([dotnet, "workload", "install", "wasm-tools"], cwd=util.repo_root())
        else:
            print("--------")
            print("Error: the .NET wasm-tools workload is not installed (needed to link the Rust staticlib into the wasm hosts).")
            print("Two ways to fix it:")
            print(f"  1. dotnet workload install wasm-tools   (needs sudo: the SDK at {sdk_root} is not writable by this user)")
            print("  2. Remove the system dotnet from consideration (or just run this tool on a machine without one): the tool bootstrap will provision a user-local SDK into ~/.dotnet, where the workload install needs no admin rights.")
            sys.exit(1)
    _warn_on_emscripten_drift(dotnet)


def emsdk_env(dotnet: str) -> dict[str, str]:
    """Environment for cargo invocations that link wasm executables: the workload's emscripten on PATH, configured exactly the way BrowserWasmApp.targets configures it (the pack's .emscripten config is entirely env-var-driven). Always the workload's emsdk, never a stray one -- the matched set (docs/wasm-toolchain.md) depends on it. Note the deliberate node split: emcc's internals run on the Node pack's node (DOTNET_EMSCRIPTEN_NODE_JS, same as the workload), while the test *runner* is PATH node -- the engine the wasm-desktop target actually ships on -- which is why the Node pack stays off PATH."""
    packs = os.path.join(os.path.dirname(os.path.realpath(dotnet)), "packs")
    sdk_major = _dotnet_major(dotnet)
    rid = _dotnet_rid()
    sdk_pack = _emscripten_pack_dir(packs, "Sdk", rid, sdk_major)
    node_pack = _emscripten_pack_dir(packs, "Node", rid, sdk_major)
    cache_pack = _emscripten_pack_dir(packs, "Cache", rid, sdk_major)
    env = dict(os.environ)
    env["PATH"] = os.pathsep.join([os.path.join(sdk_pack, "tools", "emscripten"), os.path.join(sdk_pack, "tools", "bin"), env.get("PATH", "")])
    env["DOTNET_EMSCRIPTEN_LLVM_ROOT"] = os.path.join(sdk_pack, "tools", "bin")
    env["DOTNET_EMSCRIPTEN_BINARYEN_ROOT"] = os.path.join(sdk_pack, "tools")
    env["DOTNET_EMSCRIPTEN_NODE_JS"] = os.path.join(node_pack, "tools", "bin", _exe("node"))
    env["EM_CACHE"] = os.path.join(cache_pack, "tools", "emscripten", "cache")
    env["EM_FROZEN_CACHE"] = "1"
    env["PYTHONUTF8"] = "1"
    env["EM_WORKAROUND_PYTHON_BUG_34780"] = "1"
    # Emscripten defaults EXIT_RUNTIME=0, under which a browser-hosted exit() never fires Module.onExit -- and the browser test harness reads its pass/fail from exactly that hook (wasmbrowser.py's sentinel). Applied at link by emcc; rustc only uses emcc as the linker, so this can't leak into compiles.
    env["EMCC_CFLAGS"] = "-sEXIT_RUNTIME=1"
    return env


def _dotnet_major(dotnet: str) -> int:
    try:
        result = subprocess.run([dotnet, "--version"], cwd=util.repo_root(), capture_output=True, text=True, timeout=120)
    except (subprocess.TimeoutExpired, OSError):
        result = None
    if result is None or result.returncode != 0:
        print("--------")
        print(f"Error: `{dotnet} --version` failed; cannot determine the SDK major version for emscripten pack selection.")
        sys.exit(1)
    return int(result.stdout.strip().split(".")[0])


def _dotnet_rid() -> str:
    os_part = util.platformswitch(linux="linux", windows="win", mac="osx")
    arch = {"x86_64": "x64", "AMD64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine())
    if arch is None:
        print("--------")
        print(f"Error: unrecognized machine architecture {platform.machine()!r}; cannot locate emscripten packs.")
        sys.exit(1)
    return f"{os_part}-{arch}"


def _emscripten_pack_dir(packs: str, kind: str, rid: str, sdk_major: int) -> str:
    """Resolve one emscripten pack (Sdk/Node/Cache) at the pinned version: the highest pack-version dir whose major matches the resolved SDK's major -- packs carry version dirs for several SDK bands, and a cross-band pick would violate the matched set without tripping the version pin."""
    pack_root = os.path.join(packs, f"Microsoft.NET.Runtime.Emscripten.{_EMSCRIPTEN_PIN}.{kind}.{rid}")
    if not os.path.isdir(pack_root):
        found = sorted(name for name in os.listdir(packs) if name.startswith("Microsoft.NET.Runtime.Emscripten.")) if os.path.isdir(packs) else []
        print("--------")
        print(f"Error: emscripten pack {os.path.basename(pack_root)} not found (the pinned emscripten {_EMSCRIPTEN_PIN} is part of the matched set in docs/wasm-toolchain.md).")
        print(f"Emscripten packs present: {found or 'none'} -- if the pin moved, run the re-verification checklist and update _EMSCRIPTEN_PIN.")
        sys.exit(1)
    candidates: list[tuple[tuple[int, ...], str]] = []
    for name in os.listdir(pack_root):
        numbers = tuple(int(part) for part in name.split("-")[0].split(".") if part.isdigit())
        if numbers and numbers[0] == sdk_major:
            candidates.append((numbers, name))
    if not candidates:
        print("--------")
        print(f"Error: {pack_root} has no pack-version dir matching SDK major {sdk_major} (found: {sorted(os.listdir(pack_root))}).")
        sys.exit(1)
    chosen = os.path.join(pack_root, max(candidates)[1])
    print(f"emscripten {kind} pack: {chosen}")
    return chosen


def _warn_on_emscripten_drift(dotnet: str) -> None:
    # Warn-only: a version-string mismatch is a heads-up to re-run the docs/wasm-toolchain.md checklist, not proof of breakage (and a match is not proof of health -- link-and-run is).
    packs = os.path.join(os.path.dirname(os.path.realpath(dotnet)), "packs")
    if not os.path.isdir(packs):
        return
    found: list[str] = []
    for name in os.listdir(packs):
        if name.startswith("Microsoft.NET.Runtime.Emscripten.") and ".Sdk" in name:
            found.append(name.removeprefix("Microsoft.NET.Runtime.Emscripten.").split(".Sdk")[0])
    if found and _EMSCRIPTEN_PIN not in found:
        print(f"Warning: the SDK's emscripten pack(s) {sorted(set(found))} differ from the verified pin {_EMSCRIPTEN_PIN}; run the re-verification checklist in docs/wasm-toolchain.md.")


# --- .NET SDK (auto-provision user-local; the pin lives in global.json) ---


def ensure_dotnet() -> str:
    """Resolve the .NET SDK. `dotnet --version` run at the repo root applies global.json itself and fails when unsatisfiable, so version arbitration stays in global.json -- we never reimplement rollForward. A previously-provisioned ~/.dotnet takes precedence over PATH so a distro install can't shadow the pin."""
    provisioned = os.path.join(os.path.expanduser("~"), ".dotnet", _exe("dotnet"))
    candidates = [provisioned]
    on_path = shutil.which("dotnet")
    if on_path is not None:
        candidates.append(on_path)
    for candidate in candidates:
        if os.path.exists(candidate) and _dotnet_satisfies(candidate):
            return candidate
    _install_dotnet()
    if not _dotnet_satisfies(provisioned):
        print("--------")
        print("Error: installed a .NET SDK but it still doesn't satisfy global.json; check the installer output above.")
        sys.exit(1)
    return provisioned


def _dotnet_satisfies(dotnet: str) -> bool:
    try:
        result = subprocess.run([dotnet, "--version"], cwd=util.repo_root(), capture_output=True, timeout=120)
    except (subprocess.TimeoutExpired, OSError):
        return False
    return result.returncode == 0


def _install_dotnet() -> None:
    global_json = os.path.join(util.repo_root(), "global.json")
    print("No .NET SDK satisfying global.json found; installing one user-local into ~/.dotnet (nothing system-wide is touched).")
    with tempfile.TemporaryDirectory() as tmp:
        if util.platformswitch(linux=False, windows=True, mac=False):
            script = os.path.join(tmp, "dotnet-install.ps1")
            _download("https://dot.net/v1/dotnet-install.ps1", script)
            util.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-JSonFile", global_json])
        else:
            script = os.path.join(tmp, "dotnet-install.sh")
            _download("https://dot.net/v1/dotnet-install.sh", script)
            util.run(["bash", script, "--jsonfile", global_json])
