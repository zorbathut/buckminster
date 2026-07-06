# pyright: basic
"""Typed facade over the SCons API surface we actually use. All SCons type suppressions are quarantined in this file (SCons ships no type information); the rest of the build code sees typed signatures only. This is also the migration seam if SCons ever needs replacing -- nothing outside this file touches the SCons API."""

from typing import Callable

from SCons.Script import Action, Alias, AlwaysBuild, Default  # type: ignore

# SCons node objects are opaque to callers; they only flow back into this facade.
Target = object


def phony(name: str, action: Callable[[], int]) -> Target:
    """Define an always-run named target executing `action`; a nonzero return fails the build."""

    def scons_action(target: object, source: object, env: object) -> int:
        return action()

    # cmdstr=None: the action echoes its own command; SCons's default function-call echo is just noise.
    node = Alias(name, [], Action(scons_action, cmdstr=None))
    AlwaysBuild(node)
    return node


def group(name: str, members: list[Target]) -> Target:
    """Define a named target that builds all of `members`."""
    return Alias(name, members)


def default(target: Target) -> None:
    """Mark `target` as what a bare `scons` builds."""
    Default(target)
