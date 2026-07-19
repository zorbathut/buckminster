"""The m2n staleness checker (docs/wasm-toolchain.md glossary: "same abort, but ... a NEW signature shape").

The .NET wasm build precompiles one interp-to-native trampoline per pinvoke signature shape ("cookie" strings like ILDI = i32(u64, f64, ptr)) into dotnet.native.wasm, generated via wasm_m2n_invoke.g.h. The SDK's incremental native pipeline does not track that generated file, so a new signature shape can regenerate the table on disk while the shipped binary keeps the old one -- mono then aborts at the first call, with the error text compiled out of the runtime. This module mechanizes TWO invariants that catch it, checked together by find_problems:

1. Content: every cookie in a for-* dir's wasm_m2n_invoke.g.h must appear in the sibling dotnet.native.wasm. Match rule: the cookie bytes followed by NUL, as a plain substring (cookie + b"\\0"). NOT null-delimited on both sides: the linker's string pooling stores short cookies as tails of longer strings, so a leading-NUL requirement false-alarms on genuinely-present cookies (observed on FFFF, pooled as the tail of a non-cookie string). Honest residual: short cookies (~3 chars or fewer) can be masked by coincidental tails of unrelated strings (observed: ILD via "...BUILD\\0", and the M5.75 chunk-4 incident's new f64-shape cookie against a week-stale driver.o).

2. Chain mtimes, which close that residual without inspecting bytes: the generate->compile->link chain must be monotone -- wasm_m2n_invoke.g.h is compiled into driver.o, which is linked into dotnet.native.wasm, so an input strictly newer than its output means the SDK skipped that stage. Sound because the table generation is write-if-changed at the msbuild level (verified 2026-07-19: a no-op build AND a managed-source rebuild both leave the header's mtime and hash untouched; only a genuine cookie-set change rewrites it), so a fresher header always means fresher content.

Three assumptions, each verified at time of writing and worth re-verifying at its named trigger: (1) the cookie rule's byte-level soundness leans on the -O1/no-wasm-opt link pin in src/WasmHost.props -- wasm-opt's memory packing strips zero runs from data segments, which could delete the trailing NULs this matches on; re-verify if EmccLinkOptimizationFlag changes. (2) The obj-pair binary checked here is hash-identical to the binary that actually runs (the AppBundle copy / the fingerprinted wwwroot copy); if the SDK ever transforms the binary between the obj dir and the output, the check must move to the served copy. (3) The chain rule's soundness leans on the table generation being write-if-changed (invariant 2 above); re-verify on a .NET SDK / wasm-tools workload bump -- a rewrite-every-build regression there would turn the chain check into a permanent false alarm (loud, healed by one relink per build, but a bump-blocking cost)."""

import glob
import os
import re
import shutil

_TABLE_FILE = "wasm_m2n_invoke.g.h"
_DRIVER_FILE = "driver.o"
_BINARY_FILE = "dotnet.native.wasm"
# The anchor text disambiguates; a letter-class like [IVLFD]+ would silently exempt any cookie letter mono adds later -- exactly the newest shapes, the ones most likely to be stale.
_COOKIE_PATTERN = re.compile(r'\{"(\w+)", wasm_invoke_')


def find_checkable_pairs(host_dir: str, kind: str) -> list[str]:
    """Dirs under host_dir's obj tree holding BOTH the generated m2n table and the linked binary, for the given kind ("build" or "publish"). Globbed rather than hardcoding config/TFM path segments: a TFM bump or -c Release must surface as different paths found, never as zero-checked-and-green."""
    pairs: list[str] = []
    for candidate in sorted(glob.glob(os.path.join(host_dir, "obj", "**", "wasm", f"for-{kind}"), recursive=True)):
        if os.path.isfile(os.path.join(candidate, _TABLE_FILE)) and os.path.isfile(os.path.join(candidate, _BINARY_FILE)):
            pairs.append(candidate)
    return pairs


def find_stale_cookies(pair_dir: str) -> list[str]:
    """Cookies present in the pair's generated table but missing from its binary -- the content-level staleness signature. Empty means this check passes."""
    with open(os.path.join(pair_dir, _TABLE_FILE), encoding="utf-8") as table_file:
        cookies = _COOKIE_PATTERN.findall(table_file.read())
    with open(os.path.join(pair_dir, _BINARY_FILE), "rb") as binary_file:
        binary = binary_file.read()
    return [cookie for cookie in cookies if cookie.encode() + b"\0" not in binary]


def find_stale_chain(pair_dir: str) -> str | None:
    """The mtime-level staleness signature (module doc, invariant 2): an input strictly newer than its output along table -> driver.o -> binary. None means this check passes. Float mtimes deliberately: sub-second resolution catches a skipped stage whose stale output was written within the same second as the regenerated input (equality passes either way -- a write-if-changed skip preserves the old stamp)."""
    try:
        table = os.path.getmtime(os.path.join(pair_dir, _TABLE_FILE))
        driver = os.path.getmtime(os.path.join(pair_dir, _DRIVER_FILE))
        binary = os.path.getmtime(os.path.join(pair_dir, _BINARY_FILE))
    except FileNotFoundError as missing:
        # driver.o vanishing would mean the SDK's obj layout drifted; reported as a problem (a heal's full regeneration either restores it or escalates to the hard error), never as silent-green.
        return f"chain file missing ({missing.filename}); the SDK obj layout has drifted"
    if table > driver:
        return "the m2n table is newer than driver.o (the table regenerated but the SDK skipped the recompile)"
    if driver > binary:
        return "driver.o is newer than the linked binary (the recompile ran but the SDK skipped the relink)"
    return None


def find_problems(pair_dir: str) -> list[str]:
    """Every detected staleness in one pair, human-described: cookie gaps (content-level) plus chain violations (mtime-level). Empty means the pair is consistent."""
    problems: list[str] = []
    stale = find_stale_cookies(pair_dir)
    if stale:
        problems.append(f"trampoline table has {', '.join(stale)} but the linked binary lacks them")
    chain = find_stale_chain(pair_dir)
    if chain is not None:
        problems.append(chain)
    return problems


def delete_wasm_obj_dirs(host_dirs: list[str]) -> None:
    """Removes each host's obj/**/wasm trees entirely (table, caches, objects, binary together), forcing the next build/publish to regenerate and relink from scratch -- the manual fix from the glossary, mechanized. Loud on failure."""
    for host_dir in host_dirs:
        for wasm_dir in sorted(glob.glob(os.path.join(host_dir, "obj", "**", "wasm"), recursive=True)):
            print(f"m2n heal: deleting {wasm_dir}")
            shutil.rmtree(wasm_dir)


def describe_stale(pair_dir: str, problems: list[str]) -> str:
    # Each problem string carries its own diagnosis (skipped stage vs layout drift); the shared suffix is only the pointer.
    return f"{pair_dir}: {'; '.join(problems)} (docs/wasm-toolchain.md glossary)"
