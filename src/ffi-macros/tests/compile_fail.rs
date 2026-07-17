//! The vocabulary fence, pinned: every rejection path in the buck_* macros must stay a loud, instructive compile error. A refactor that silently widens the vocabulary (or degrades a message into trait-bound soup) fails these snapshots.

// Host-only: macro expansion happens at compile time on the host regardless of target, and trybuild spawns cargo -- impossible under the emscripten cells (ENOSYS). The wasm matrix builds this file into an empty test binary and moves on.
#[cfg(not(target_os = "emscripten"))]
#[test]
fn rejections_are_loud_compile_errors() {
    let cases = trybuild::TestCases::new();
    cases.compile_fail("tests/compile_fail/*.rs");
}
