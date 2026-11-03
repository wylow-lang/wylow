<a id="library"></a>
# Appendix A. The standard library the language names

<a id="library.names"></a>
## A.1 The names

The standard library is the package `std`, compiled with every program. The language relies on
these of its declarations, and on nothing else in it:

| Declaration | What the language uses it for |
|---|---|
| `String` | The type of string literals ([`String`](04-types.md#types.string)) |
| `Array<T>` | The type `T[]` ([Arrays](04-types.md#types.arrays)) |
| `Span<T>`, `ReadOnlySpan<T>` | Span types and their conversions ([Spans](04-types.md#types.spans)) |
| `Spannable<T>`, `ReadOnlySpannable<T>` | Conversions to spans ([`Spannable`](#library.spannable)) |
| `Borrowable` | Borrows ([`Borrowable`](17-memory.md#memory.borrows.borrowable)) |
| `Printable` | String interpolation ([`Printable`](#library.printable)) |
| `Comparable<T>` | Ordering of the built-in types ([`Comparable`](#library.comparable)) |
| `Result<T, E>` | `try` ([`Result`](18-errors.md#errors.result)) |
| `Weak<T>` | Weak references ([Weak references](17-memory.md#memory.weak)) |
| `Isolated<T>` | Isolated object graphs ([Isolated object graphs](19-concurrency.md#concurrency.isolated)) |
| `std::panic` | Panics ([`std::panic`](#library.panic)) |
| `std::memory` | Raw memory and the cycle collector ([`std::memory`](#library.memory)) |

A program may declare its own type with the name of any other declaration of `std`.

The language calls the methods of some of these interfaces: `Printable`'s `toString` in string
interpolation, `ReadOnlySpannable`'s `asReadOnlySpan` and `Spannable`'s `asSpan` in conversions to
spans and in for-each loops, and `Borrowable`'s `beginBorrow` and `endBorrow` around borrows. A
type takes part by implementing the interface. Each interface's section says which types may
implement it and whether a program may call its methods.

<a id="library.spannable"></a>
## A.2 `Spannable` and `ReadOnlySpannable`

<a id="library.printable"></a>
## A.3 `Printable`

<a id="library.comparable"></a>
## A.4 `Comparable`

<a id="library.panic"></a>
## A.5 `std::panic`

<a id="library.memory"></a>
## A.6 `std::memory`
