"""The m2n staleness checker (docs/wasm-toolchain.md glossary: "same abort, but ... a NEW signature shape").

The .NET wasm build precompiles one interp-to-native trampoline per pinvoke signature shape ("cookie" strings like ILDI = i32(u64, f64, ptr)) into dotnet.native.wasm, generated via wasm_m2n_invoke.g.h. The SDK's incremental native relink does not track that generated file, so a new signature shape can regenerate the table on disk while the shipped binary keeps the old one -- mono then aborts at the first call, with the error text compiled out of the runtime. This module mechanizes the invariant that catches it: every cookie in a for-* dir's wasm_m2n_invoke.g.h must appear in the sibling dotnet.native.wasm.

Match rule: the cookie bytes followed by NUL, as a plain substring (cookie + b"\\0"). NOT null-delimited on both sides: the linker's string pooling stores short cookies as tails of longer strings, so a leading-NUL requirement false-alarms on genuinely-present cookies (observed on FFFF, pooled as the tail of a non-cookie string). This is the strongest rule with no false alarms. Honest residual: short cookies (~3 chars or fewer) can be masked by coincidental tails of unrelated strings (observed: ILD matches via "...BUILD\\0") -- accepted, because a false pass merely reproduces the status quo this checker exists to improve (the runtime abort, diagnosed via the glossary), never a new failure mode.

Two assumptions, both verified at time of writing and worth re-verifying if things drift: (1) the rule's byte-level soundness leans on the -O1/no-wasm-opt link pin in src/WasmHost.props -- wasm-opt's memory packing strips zero runs from data segments, which could delete the trailing NULs this matches on; re-verify if EmccLinkOptimizationFlag changes. (2) The obj-pair binary checked here is hash-identical to the binary that actually runs (the AppBundle copy / the fingerprinted wwwroot copy); if the SDK ever transforms the binary between the obj dir and the output, the check must move to the served copy."""

import glob
import os
import re
import shutil

_TABLE_FILE = "wasm_m2n_invoke.g.h"
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
    """Cookies present in the pair's generated table but missing from its binary -- the staleness signature. Empty means this pair is consistent."""
    with open(os.path.join(pair_dir, _TABLE_FILE), encoding="utf-8") as table_file:
        cookies = _COOKIE_PATTERN.findall(table_file.read())
    with open(os.path.join(pair_dir, _BINARY_FILE), "rb") as binary_file:
        binary = binary_file.read()
    return [cookie for cookie in cookies if cookie.encode() + b"\0" not in binary]


def delete_wasm_obj_dirs(host_dirs: list[str]) -> None:
    """Removes each host's obj/**/wasm trees entirely (table, caches, objects, binary together), forcing the next build/publish to regenerate and relink from scratch -- the manual fix from the glossary, mechanized. Loud on failure."""
    for host_dir in host_dirs:
        for wasm_dir in sorted(glob.glob(os.path.join(host_dir, "obj", "**", "wasm"), recursive=True)):
            print(f"m2n heal: deleting {wasm_dir}")
            shutil.rmtree(wasm_dir)


def describe_stale(pair_dir: str, stale: list[str]) -> str:
    return f"{pair_dir}: trampoline table has {', '.join(stale)} but the linked binary lacks them (the SDK incremental relink didn't rerun -- docs/wasm-toolchain.md glossary)"
