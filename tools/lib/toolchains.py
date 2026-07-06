"""Toolchain audit and user-local auto-provisioning. Policy (PLAN.md): the install floor is git + Python + platform C++ build tools; everything else is either auto-provisioned user-local here (pinned, no admin rights) or audited with exact install instructions on failure."""

import dataclasses
import json
import os
import platform
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
    return Toolchains(cargo=ensure_cargo(), dotnet=ensure_dotnet())


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
