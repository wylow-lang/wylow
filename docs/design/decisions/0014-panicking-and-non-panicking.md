# 0014. Panics are for bugs; the library has non-panicking versions

- Status: accepted (2026-10-20)
- Spec: `errors.panics`; the standard library's doc comments

## Decision

A panic means the code has a bug. Panics are not exceptions: expected failures (a missing file, bad
input, a lookup that may find nothing) are values, `T?` or `Result<T, E>`.

**Rule:** every standard library operation that panics on a value its caller may not control has a
non-panicking version. It returns `T?` when there is one way to fail, and `Result<T, E>` when the
caller needs to know which way it failed. The panicking version stays the short, obvious one, for
code where failure would be a bug.

| Panics | Doesn't |
|---|---|
| `list[i]`, `array[i]`, `span[i]` | `list.get(i)`, `array.get(i)`, `span.get(i)`: a `ref? T` (`in? T` for a `ReadOnlySpan`), null if `i` is out of bounds |
| `list.removeAt(i)` | `list.tryRemoveAt(i)`: a `T?` |
| `list.insert(i, v)` | none: an insertion index is the caller's own arithmetic, so a bad one is a bug |
| `(Color)i` | `Color.tryFrom(i)`: a `Color?` |
| `x!` | `x ?? fallback`, or `if (x is T v)` |
| `r.orPanic("message")` | `r.orElse(fallback)`, `switch`, or `try` |

Non-panicking versions are named `tryX` after the panicking `x`, except where a shorter name is
established (`get` for indexers, `??` and patterns for nullable values).

Panics on state the caller controls itself, such as changing a borrowed list, have no
non-panicking version: they are always bugs.

`get` returns a nullable reference rather than a copy, so that a lookup that fails is as cheap as
the indexer and what it finds can be changed in place; `list.get(i)!` copies the element where a
copy is wanted.

## Alternatives

- **Unchecked exceptions** for expected failures, such as Java's run-time exceptions
  ([JLS 11.1.1](https://docs.oracle.com/javase/specs/jls/se27/html/jls-11.html#jls-11.1.1)): every call may then fail invisibly.
- **Only non-panicking operations**: every index would need handling at every use.

## Why

An operation should panic only when its failure means the caller made a mistake. When a caller may
reasonably not know whether an operation will succeed, because the input came from outside its
control, it needs a version that reports failure as a value. This is .NET's
[`Int32.Parse`](https://learn.microsoft.com/en-us/dotnet/api/system.int32.parse) and
[`Int32.TryParse`](https://learn.microsoft.com/en-us/dotnet/api/system.int32.tryparse), with `T?`
or `Result` in place of an `out` parameter.
