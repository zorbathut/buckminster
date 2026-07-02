# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Buckminster is a microkernel-inspired game engine written in Rust and C#. Dual-language: performance-critical/core engine work in Rust, higher-level systems in C#. MIT licensed.

(Early-stage project — build commands and architecture documentation will be added as the codebase takes shape. When an ARCHITECTURE.md exists, reference it here.)

## Interaction Guidelines

**Answer questions before coding**: When asked a question, provide an actual answer first. Don't leap straight to writing code.

**Evaluate, don't assume**: "Why don't we X?" is a request for evaluation, not a suggestion to do X. Explain the tradeoffs, potential issues, or reasons why X might or might not be a good idea.

**Debug by evidence, not by guess**: When investigating a bug you don't fully understand, prefer adding diagnostic instrumentation or asking focused questions over making speculative changes. A confident theory backed by reading the code is fine to act on; a vibe is not. If a fix doesn't solve the user's problem, that's a signal that the theory was wrong — gather more data before trying again. Two consecutive failed fixes mean stop guessing entirely: pause, instrument, and ask. Rapid-fire blind changes waste the user's attention and erode trust.

**Err on the side of more diagnostic data, not less**: When you ask the user to run something — a probe build, a manual test, a copy-paste session — the expensive part is the round trip itself. The marginal cost of one more printed value, one more covered code path, one more chapter to click is small. So when you instrument, instrument generously: log every variable that could plausibly disambiguate the bug, exercise every endpoint of the parameter space (V=0, V=0.5, V=1, not just whichever was easy), include both the suspected-correct prediction *and* the alternatives so residuals are immediately visible. A diagnostic that prints 30 lines and answers the question on the first try is far cheaper than three diagnostics that each print 3 lines. Make the round trip pay for itself.

**Waiting on background work is not a tool call**: When you've backgrounded a long command (e.g. the full test suite, which exceeds the foreground timeout) and have nothing else productive to do, just end the turn — its completion notification will re-invoke you automatically. Don't emit no-op commands (`echo "waiting"`, re-reads, status pings) to stay "active"; ending on plain text is the correct way to wait, not a hand-off. Conversely, if a command fits the foreground timeout and you'd only wait for it anyway, run it in the foreground so the result returns in the same call. Backgrounding *and* polling is the worst of both.

## Workflow

**Step 1 — Plan.** Enter plan mode (the actual `EnterPlanMode` tool — not a freeform text plan) and research the task and produce a plan. Skippable for trivial changes (under ~a dozen lines). Include unit tests in the plan whenever they're plausible to add — UI generally can't be tested, most other things can.

**Step 2 — Hostile-review the plan.** Before leaving plan mode, spawn a hostile-review agent against the plan itself. Brief it like a design reviewer: explain the problem being solved, point it at CLAUDE.md (and the rest of the tree — it can read whatever it needs to research), give it the plan, but do not justify the plan's choices. Give it enough feedback space to actually push back on the approach. Apply the same adjudication rules as the final review (below). Fold valid objections into the plan, then exit plan mode.

**Step 3 — Tests first (when applicable).** For bugfixes, or any feature whose tests can be sensibly written before the implementation exists, write the tests first and verify they fail. Then complete the implementation.

**Step 4 — Run all tests.** Always, even when the change seems unrelated. If anything breaks, return to step 3 — or step 1 if the fix requires significant redesign. For UI changes that can't be unit-tested, explicitly say so rather than claiming success.

Don't treat a failing test as a hard veto on the change. Tests exist to catch *unintentional* drift — a test that pins behavior the change deliberately replaced should be updated alongside the code, not worked around to preserve the old behavior. Fix the test to match the new intent; only fall back to step 3 / step 1 when the failure exposes an actual regression.

**Step 5 — Update CHANGELOG.** For every even-slightly-user-facing change — new/changed/removed APIs, behavior changes, bugfixes, diagnostics the user sees, doc comments on public members, performance characteristics — add an entry under `[unreleased]` in the appropriate section (Added / Breaking / Improved / Fixed). Purely internal cleanup with no outward effect (private helpers, test-only code, internal comments) can be skipped. When in doubt, add the entry.

**Step 6 — Hostile review.** Spawn a hostile-review agent. Brief it like a PR reviewer: explain the problem being solved, point it at CLAUDE.md (and the rest of the tree — it can read whatever it needs to research), but do not explain or justify the implementation. Explicitly ask it to **review the general architecture** too, not just the diff — does the chosen approach fit the surrounding code, are there cleaner factorings, does it introduce abstractions that don't pay rent, etc. Give it enough feedback space to cover both the local change and the architectural read effectively (don't cap it to a terse response). Then:
  - If it raises valid objections, fix them. Significant redesign → back to step 1; code changes → back to step 3.
  - If I disagree with an objection, push back once. If it still objects and I'm still confident, surface the disagreement to the user for adjudication rather than looping.
  - Either way — adjudication needed or not — give the user a quick summary of the review at the end.

## Code Patterns and Conventions

### Naming Conventions

- **C#**: PascalCase for public members, types, and static fields; camelCase for private/protected fields and parameters; `I` prefix for interfaces (e.g., `IComponent`).
- **Rust**: Standard Rust conventions — snake_case for functions/variables/modules, PascalCase for types/traits/enum variants, SCREAMING_SNAKE_CASE for constants. Follow rustfmt and clippy defaults.
- **Category-instance prefix** (both languages): When a name combines a category with an instance, put the category first so related names group alphabetically and the category reads as the classification. `SpawnerBurst`, `ShapeRadial`, `AttackStart()` — not `BurstSpawner`, `RadialShape`, `StartAttack()`. The category is the "kind of thing"; the instance is the specific variant.

### Critical Rules

1. **Always use absolute paths** in file operations.

### Coding Guidelines

**KISS/YAGNI**: Keep it simple. Don't build abstractions or features that aren't immediately needed. Write the simplest code that solves the current problem.

**No backwards compatibility**: Remove stubs and dead code completely. Don't preserve backwards compatibility for its own sake—if something is unused or being replaced, delete it outright.

**Default parameters and overloads (C#)**: The deciding axis is the *nature of the parameter*, not the mechanism. A default parameter is the right tool for a conceptually optional thing; an overload is for a signature that is genuinely different, not "a default parameter wearing a funny hat."

- **A new parameter the function genuinely needs is mandatory.** Add it without a default and update the call sites. Don't reflexively give every new parameter a default value just to avoid touching callers — that's the main thing this rule exists to prevent.
- **A default value is for a *conceptually optional* parameter** — one with a principled "absent" value: a nullable callback or override (`Action onDone = null`), or a natural identity like `double steepness = 1.0`. It is *not* for an arbitrary tuning constant that merely happens to suit most callers — something like `attemptsPerIteration = 30` should be mandatory or a named constant, not a default.
- **Overloads are for genuinely different signatures** — different parameter *types* or *shapes* that can't collapse into one signature, or a meaningfully different operation. Do not write an overload pair whose only difference is that one omits a trailing optional argument — use a default parameter instead. (For example, `Sigmoid(x)` + `Sigmoid(x, steepness)` should collapse to one `Sigmoid(double x, double steepness = 1.0)` — the natural-identity case above.)
- **A behavior-switching bool may be a default-`false` parameter only when it's a rider on the same operation** — the result is the same kind of thing, the flag just tweaks a side aspect, and it's almost always off ("sweep, *and while you're at it* ignore platforms": `Sweep(…, bool ignorePlatforms = false)`). When the flag changes *what the function fundamentally means* — the question it answers — it shouldn't be a flag: make it a separate, differently-named function, or handle it at the call site. Name any split function category first, per Naming Conventions.
- **Hard exception**: compiler-attribute parameters (`[CallerFilePath]`, `[CallerLineNumber]`, `[CallerMemberName]`) must be default parameters — there is no overload form, so these don't count against the rule.

The same spirit applies in Rust, which has neither defaults nor overloads: don't reach for `Option<T>` parameters or builder methods to paper over a parameter the function genuinely needs — make it mandatory and update the call sites.

**Always use braces (C#)**: Always include `{}` for `if`, `else`, `for`, `foreach`, `while`, etc., even for single-line bodies. (Rust enforces this at the language level.)
```csharp
// Good
if (condition)
{
    return;
}

// Bad
if (condition) return;
if (condition)
    return;
```

**Avoid expression-bodied members (`=>`) (C#)**: Prefer block bodies with explicit `return` statements for methods and properties. Expression bodies obscure control flow.
```csharp
// Good
public int GetValue()
{
    return value;
}

// Bad
public int GetValue() => value;
```

**Error handling**:
- Don't add excessive or preemptive error handling. Don't validate everything before it's ever been an issue.
- **Silent error handling is banned.** Never swallow exceptions or ignore error conditions. If something fails, it must be reported or thrown. In Rust terms: don't discard `Result`s (`let _ = fallible()`, `.ok()` used to drop an error) — propagate with `?`, handle explicitly, or log.

**Don't hand-wrap lines**: One thought, one line — however long. Editors soft-wrap; you don't need to. The only exceptions are:
- **Distinct paragraphs** in a comment: separate with a **blank line** (true paragraph break), not just a `\n`.
- **Structurally-aligned expressions**: one argument per line, one chained call per line, etc.

A multi-sentence single-thought comment is still one line. "It reads better wrapped" is not an exception — that's the rule talking.

### Commenting

A comment earns its place by saying something the code cannot. That's usually one of: a non-obvious "why", a subtle constraint, a surprising choice or tradeoff — or signposting the flow of a long linear process. A one-line summary of what the next chunk of a long function is doing ("Accumulate the asymptotes" over ten lines of dense math; "Resolve overlaps, nearest first" over a loop) is genuinely useful, and often cleaner than extracting that chunk into a function called exactly once. A comment that restates what the name or a single line of code already makes plain is noise.

**Adding — be conservative, but signpost freely.** Don't narrate the obvious: a `bool allowFlips` field needs no `// when false, flipping is disabled`; a `// set the flip` above `flip = …` adds nothing. Reach first for self-explanatory code (good names, clear structure), and comment the part code can't carry — usually the *why*, not the *what*. The exception is flow signposting in long procedures, where a sparse trail of one-line "what next" headers is a real readability win; use them. When you explain a "why", keep it tight; one good line beats a paragraph.

**Removing — be generous about keeping.** Existing comments are there for a reason. If one is out of date or actively misleading, fix or remove it. Otherwise leave it alone — don't strip a comment just because it explains the "what", or because you wouldn't have written it yourself. The asymmetry is deliberate: conservative about adding your own, generous about keeping others'.
