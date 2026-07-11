"""Shared browser-test driver: serve a directory over localhost, load a page in headless Chrome, and read the DOM-sentinel protocol -- the page buffers all output into <pre id="out"> and appends BUCK-TEST-EXIT:<code> as its final line. Two producers (the Rust libtest harness page generated here, and the C# web host's main.js), one driver.

Why CDP instead of --dump-dom: --dump-dom's snapshot timing is coupled to virtual-time heuristics that promise nothing about async wasm work, so a page finishing "shortly after load" races the dump. Driving Chrome over --remote-debugging-pipe (CDP as \\0-framed JSON over inherited fds 3/4 -- no websockets, no extra binaries) replaces the race with an explicit contract: poll the live DOM until the sentinel appears or a real timeout expires, and a hung page fails loudly with whatever it did produce."""

import sys

if sys.platform != "win32":
    import fcntl
import functools
import http.server
import json
import os
import re
import select
import shutil
import subprocess
import threading
import time

from lib import util


class _HandlerQuiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, format: str, *args: object) -> None:
        # Per-request logging is noise; failures surface through the captured DOM, not the access log.
        pass


def find_chrome() -> str | None:
    override = os.environ.get("BUCK_CHROME")
    if override is not None:
        return override
    for name in ["google-chrome-stable", "google-chrome", "chromium", "chromium-browser"]:
        found = shutil.which(name)
        if found is not None:
            return found
    return None


def chrome_missing_message() -> str:
    return "no Chrome/Chromium found -- install google-chrome-stable or chromium, or point BUCK_CHROME at a browser binary"


class _ChromePipe:
    """Minimal CDP client over --remote-debugging-pipe: chrome reads commands on its fd 3 and writes responses on its fd 4, each message a \\0-terminated JSON blob. Enough protocol for navigate + evaluate; nothing more."""

    def __init__(self, chrome: str) -> None:
        if sys.platform == "win32":
            raise RuntimeError("the browser test driver is POSIX-only for now (CDP over inherited fds); the Windows story lands with the M5 CI build check at the earliest")
        command_read, self._command_write = os.pipe()
        self._response_read, response_write = os.pipe()
        # Hoist every pipe fd above the 3/4 claim range: with few files open, os.pipe() hands out 3 and 4 themselves, and the claim loop below would close the very ends it's installing.
        command_read = self._hoist(command_read)
        self._command_write = self._hoist(self._command_write)
        self._response_read = self._hoist(self._response_read)
        response_write = self._hoist(response_write)
        # Chrome expects its pipe ends at exactly fds 3 and 4, and subprocess closes any child fd not listed in pass_fds (even ones a preexec_fn dup2'd into place). So claim 3/4 in the parent for the spawn window and restore after. Caller contract: construct before starting any thread that churns fds (run_page constructs the client before the HTTP server).
        saved: list[tuple[int, int | None]] = []
        for source, target in ((command_read, 3), (response_write, 4)):
            try:
                saved.append((target, os.dup(target)))
            except OSError:
                saved.append((target, None))
            os.dup2(source, target)
            os.close(source)
        try:
            self._process = subprocess.Popen(
                [chrome, "--headless=new", "--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage", "--remote-debugging-pipe", "about:blank"],
                pass_fds=(3, 4),
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        finally:
            for target, original in saved:
                if original is None:
                    os.close(target)
                else:
                    os.dup2(original, target)
                    os.close(original)
        self._buffer = b""
        self._next_id = 1

    @staticmethod
    def _hoist(fd: int) -> int:
        if fd > 4:
            return fd
        hoisted = fcntl.fcntl(fd, fcntl.F_DUPFD, 5)
        os.close(fd)
        return hoisted

    def command(self, method: str, params: dict[str, object] | None = None, session: str | None = None, timeout: float = 30) -> dict[str, object]:
        message_id = self._next_id
        self._next_id += 1
        payload: dict[str, object] = {"id": message_id, "method": method, "params": params or {}}
        if session is not None:
            payload["sessionId"] = session
        os.write(self._command_write, json.dumps(payload).encode() + b"\0")
        deadline = time.monotonic() + timeout
        while True:
            if time.monotonic() > deadline:
                # Checked here too: a stream of unsolicited events could otherwise keep the loop fed past the deadline.
                raise TimeoutError(f"CDP {method} timed out")
            message = self._read_message(deadline)
            if message.get("id") == message_id:
                if "error" in message:
                    raise RuntimeError(f"CDP {method} failed: {message['error']}")
                result = message.get("result")
                assert isinstance(result, dict)
                return result  # pyright: ignore[reportUnknownVariableType]
            # Events and other sessions' traffic are irrelevant to this driver; skip.

    def _read_message(self, deadline: float) -> dict[str, object]:
        while b"\0" not in self._buffer:
            # select before read: a wedged-but-alive chrome would otherwise block os.read forever, and no timeout would ever fire.
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([self._response_read], [], [], remaining)[0]:
                raise TimeoutError("CDP response timed out")
            chunk = os.read(self._response_read, 65536)
            if not chunk:
                raise RuntimeError("chrome closed the CDP pipe")
            self._buffer += chunk
        raw, self._buffer = self._buffer.split(b"\0", 1)
        parsed = json.loads(raw)
        assert isinstance(parsed, dict)
        return parsed  # pyright: ignore[reportUnknownVariableType]

    def close(self) -> None:
        try:
            os.close(self._command_write)
            os.close(self._response_read)
        except OSError:
            pass
        try:
            self._process.terminate()
            self._process.wait(timeout=10)
        except (subprocess.TimeoutExpired, OSError):
            self._process.kill()


def run_page(chrome: str, serve_dir: str, page: str, timeout: float = 120) -> tuple[int | None, str]:
    """Serve `serve_dir`, load `page` in headless Chrome, poll the DOM until the sentinel appears or `timeout` (real seconds) expires. Returns (sentinel exit code or None, captured <pre id="out"> text). A None code should always be treated as failure; the captured text is the diagnostic."""
    client = _ChromePipe(chrome)
    handler = functools.partial(_HandlerQuiet, directory=serve_dir)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    text = ""
    code: int | None = None
    try:
        target = client.command("Target.createTarget", {"url": f"http://127.0.0.1:{port}/{page}"})
        attach = client.command("Target.attachToTarget", {"targetId": target["targetId"], "flatten": True})
        session = attach["sessionId"]
        assert isinstance(session, str)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = client.command("Runtime.evaluate", {"expression": "(document.getElementById('out') || {}).textContent || ''", "returnByValue": True}, session=session)
            value = result.get("result")
            raw = value.get("value") if isinstance(value, dict) else None  # pyright: ignore[reportUnknownMemberType, reportUnknownVariableType]
            text = raw if isinstance(raw, str) else ""
            sentinel = re.search(r"BUCK-TEST-EXIT:(-?\d+)\s*$", text.strip())
            if sentinel is not None:
                code = int(sentinel.group(1))
                break
            time.sleep(0.25)
    finally:
        client.close()
        server.shutdown()
        server.server_close()
        thread.join(timeout=10)
    return (code, text)


# The Rust harness page: classic (non-MODULARIZE) emscripten output reads the global Module for its stdio and exit hooks. libtest's own "test result:" line lands in the pre either way -- the diagnostic breadcrumb if the sentinel never arrives.
_PAGE_RUST = """<!DOCTYPE html>
<html>
<head><meta charset="UTF-8"><title>{name}</title></head>
<body>
<pre id="out"></pre>
<script>
// buckTestOut, not "out": emscripten's shell.js declares a global `var out` (its print fn) in the same scope and clobbers any page variable of that name -- learned the hard way.
var buckTestOut = document.getElementById('out');
function append(line) {{ buckTestOut.textContent += line + "\\n"; }}
window.onerror = function (message, source, line) {{ append("ONERROR: " + message + " @" + source + ":" + line); append("BUCK-TEST-EXIT:101"); }};
var Module = {{
    print: append,
    printErr: append,
    onExit: function (code) {{ append("BUCK-TEST-EXIT:" + code); }},
    onAbort: function (what) {{ append("ABORT: " + what); append("BUCK-TEST-EXIT:102"); }}
}};
</script>
<script src="{js}"></script>
</body>
</html>
"""


def list_rust_wasm_test_binaries(cargo: str, env: dict[str, str]) -> list[str]:
    """Paths of the wasm test executables (.js files), via cargo's JSON messages -- target/deps globbing lies when stale hash-suffixed binaries accumulate."""
    result = subprocess.run(
        [cargo, "test", "--workspace", "--target", "wasm32-unknown-emscripten", "--no-run", "--message-format=json"],
        cwd=os.path.join(util.repo_root(), "src"), env=env, capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        print(result.stderr)
        raise RuntimeError("cargo test --no-run failed while enumerating wasm test binaries")
    binaries: list[str] = []
    for line in result.stdout.splitlines():
        message = json.loads(line)
        if message.get("reason") == "compiler-artifact" and message.get("profile", {}).get("test") and message.get("executable"):
            binaries.append(message["executable"])
    return binaries


def stage_rust_test(js_path: str) -> str:
    """Copy one test binary's .js/.wasm pair into a staging dir with a generated harness page; returns the dir."""
    name = os.path.splitext(os.path.basename(js_path))[0]
    staging = os.path.join(util.repo_root(), "src", "target", "browser-rust-tests", name)
    os.makedirs(staging, exist_ok=True)
    shutil.copy2(js_path, staging)
    shutil.copy2(os.path.splitext(js_path)[0] + ".wasm", staging)
    with open(os.path.join(staging, "index.html"), "w") as page:
        page.write(_PAGE_RUST.format(name=name, js=os.path.basename(js_path)))
    return staging
