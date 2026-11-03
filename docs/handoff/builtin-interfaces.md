# Next: a design session on the interfaces the compiler treats specially

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. This comes **before the
chapter 4 rewrite** (`spec-chapters.md`), because chapter 4 writes `types.copyable` and chapter 1's
term table points there.

## The question

Several interfaces get behavior no ordinary interface could have: types satisfy them without
declaring it, or the compiler calls their methods on its own. The user's concern (2026-11-02): this
reads as compiler magic standing in for a missing underlying system. `T extends Copyable` is the
example: `ReadOnlySpan<T>` never says it implements `Copyable`, yet it satisfies the bound. Decide
whether each special case is one mechanism the language should name, or should go.

## What the code and spec do now (checked 2026-11-02)

| Name | Declared in `std` | What is special | Where |
|---|---|---|---|
| `Copyable` | **No**: not declared, not implemented | Every type satisfies it except unique types and type parameters not bounded by it; nothing implements it by declaring so | old `02-types.md` §2.9 ("Unique structs") and §2.13 ("Bounds"), decision 0009; `programs.builtin-names` and `library.names` list it |
| sendable | No name at all | An inferred property of a type, from its fields | old `05-concurrency.md` §5.2, decision 0015. No bound can name it, so it's unclear how generic code (a channel's or `Mutex`'s `T`) asks for it |
| `Printable` | `printable.wyl:5` | Primitives, `String`, and their nullables satisfy it without implementing it | `StdlibSymbols.IsPrintable`, `SatisfiesBound` |
| `Comparable<T>` | `comparable.wyl:6` | The numbers, `char`, and `String` are `Comparable` of themselves without implementing it; the runtime compares them | `StdlibSymbols.IsBuiltinComparable`, `SatisfiesBound`, `IsComparableCompareTo` |
| `Borrowable` | `borrowable.wyl:5` | The compiler calls `beginBorrow`/`endBorrow` around every borrow of a class that implements it; only classes may | `Callees.cs:49` and 6 other uses, `BorrowableNonClass` |
| `Spannable<T>`, `ReadOnlySpannable<T>` | `spannable.wyl` | A type implementing them converts implicitly to a span and can be iterated; a `std` class implementing `ReadOnlySpannable` has its elements visited by the cycle collector | `Binder.Spans.cs:37`, `StdlibSymbols.ContainerElement` |
| `String`, `Weak<T>` | `extern struct` | Built-in value types whose members `std` declares | `string.wyl`, `weak.wyl` |
| `Equatable<T>` | Not yet | Planned (issue #14) | |

So there are three kinds of special case, which the session should keep apart:

1. **Inferred properties of a type** (`Copyable`, sendable). They are structural facts the compiler
   computes, not interfaces anyone implements. One has a name usable as a bound and the other has
   none.
2. **Built-in types that conform without declaring it** (`Printable` and `Comparable` on primitives
   and `String`). The primitives have no declaration in which to write `implements`. `String`
   already has one (`extern struct String`), and could declare its conformances there.
3. **Hook interfaces whose methods the compiler calls** (`Borrowable`, `Spannable`). These are
   ordinary interfaces that types implement explicitly; what's special is that the compiler calls
   them.

## Directions to weigh (proposals, none decided)

- **Name inferred properties as their own concept, not interfaces.** For example, a separate kind
  of bound for compiler-computed properties, used the same way for copyability and sendability, so
  `Copyable` stops posing as an interface no one implements. Decide the syntax, and which way the
  default goes: an unbounded `T` may be unique today, so copying needs a bound.
- **Declare the primitives' conformances in `std`**: an `extern struct` (or another declaration
  form) per primitive, saying `implements Printable, Comparable<int>`, as `String`'s could. The
  special cases in `StdlibSymbols` would then become ordinary conformance checks.
- **Keep hook interfaces, and say so in one place.** They are explicit, so the question is only
  whether the spec should define "an interface the language calls" once (appendix A does, per
  interface), and whether a user's class may implement them.

Leads to check before citing them, with primary sources, in the decision record's Alternatives
(none checked yet):
- Swift: every type is implicitly `Copyable`, and `~Copyable` suppresses it (SE-0390, and the
  noncopyable generics proposal).
- Rust: `Send` and `Sync` are auto traits, while `Copy` is opted into with `derive` (the Rust
  Reference, "Special types and traits").
- Go: interfaces are satisfied structurally, with no `implements`.

## Outcome expected

A decision record (0036 onward) per mechanism, with its rules in the old spec files' rows (or in
chapter 4 when it is written), issues #6 and #14 updated for what changes, and `spec-chapters.md`
continuing with chapters 4 to 6.
