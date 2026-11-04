# 0039. `loop` replaces `do`, and a `for` has a condition

- Status: accepted (2026-11-03)
- Spec: `statements.loops`, `lexical.keywords`, `flow.definite-assignment`

## Decision

The language has `while`, `loop`, `for`, and for-each. `do` is removed and is no longer reserved. It is a compile-time error (`WYL0438`) if a `for` has no condition.

## Alternatives

- **Keep `do`.** It was specified but never implemented.
- **Remove `do` and add nothing**: `while (true)` with a `break` covers it, with no new keyword.
- **Add `loop` and keep the condition-less `for`**: a third spelling of a loop that tests nothing.

## Why

Rust's [loop expressions](https://doc.rust-lang.org/reference/expressions/loop-expr.html) are `loop`, `while`, and `for`, with no do-while: a loop that tests at the end is a `loop` with a `break`, which also lets the test sit anywhere in the body. `while (true)` stays legal, since `true` is an ordinary condition, so a single spelling of an endless loop is not enforced. `loop` is a new keyword, so a program can no longer use it as a name.
