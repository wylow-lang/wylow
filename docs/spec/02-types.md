# 2. Types

## 2.10 Strings

`String` is an immutable, UTF-8, reference-counted **built-in value type**: its bytes live in one
counted block that its copies share (it may cross threads; see
[Concurrency](19-concurrency.md#concurrency)). The standard library declares it as an
`extern struct`, which the runtime implements; `String` and `Weak<T>` are the only such built-in
values.

Strings are joined by [interpolation](13-expressions.md#expressions.interpolation), `$"{a}{b}"`, which
builds the result in one allocation. There is no `+` on strings.
`StringBuilder` builds a string up piece by piece, as in a loop.

A literal refers to static text and never allocates; a string built at run time, as by
[interpolation](13-expressions.md#expressions.interpolation), is one counted allocation shared by its copies.

`==` and `!=` compare contents ([equality](04-types.md#types.string)).

A string is stored as UTF-8 bytes, and **offsets and lengths count bytes**, never characters: a
`char` (a code point) takes 1 to 4 bytes.:

```java
String word = "héllo";
word.byteLength()           // 6: 'é' takes two bytes, read in O(1)
word.charCount()            // 5: counted by reading the whole string
word.isEmpty()              // false
word.indexOf("l")           // 3, an int?: null when not found
word.slice(1, 2)            // "é": 2 bytes from offset 1, copied into a new string
word.startsWith("hé")       // also endsWith and contains
"a".compareTo("b")          // negative, zero, or positive
```

- `slice(start, length)` takes a byte offset and length like `Span.slice`, and panics if the
  range is out of bounds or would split a character, so a string is always valid UTF-8. A slice
  of the whole string is the string itself; any other is copied, keeping every string
  null-terminated ([Interop strings](21-interop.md#interop.strings)).
- `indexOf` returns the byte offset of the first occurrence, which is where `slice` can start.
  An empty string occurs at offset 0, and every string contains it.
- `compareTo` orders by byte, which for UTF-8 is the order of the code points. It doesn't apply
  locale rules or Unicode normalization: `"é"` written as one code point and as `e` plus a
  combining accent are different strings.
- `String` has no indexer, since neither a byte nor a code point is what `s[i]` would obviously
  mean. `s.bytes()` is a `ReadOnlySpan<byte>` of the UTF-8 bytes, without a copy: `s.bytes()[0]`,
  `for (byte b : s.bytes())`. Like any span ([Spans](#spans)) it holds what it views, here the
  string, so the bytes stay valid even if the variable it came from is reassigned. A method can't
  return a view of a string field, which could be replaced while the caller uses it, as for an
  object field.
- A for-each loop over a string decodes its characters, and its index is each one's byte offset,
  which `slice` takes. The loop reads the string it started with, even if the variable is
  reassigned in the body. The characters are decoded copies, so they can't be borrowed with `ref`
  or `in`.

```java
for (char c : name) { ... }
for (int at, char c : path) {
    if (c == '/') { lastSlash = at; }
}
```

## 2.13 Generics

### Bounds

`T extends A & B` requires every type argument for `T` to implement the interfaces `A` and `B`. A
bound is an interface or a type property (below). Inside the generic, `T` has the bounds' methods. A
bound may mention the type parameters, as in `T extends Comparable<T>`.

- Both structs and classes satisfy a bound by implementing the interface. Calls through a bound use
  static dispatch for structs. For classes they go through the interface.
- A nullable type such as `Player?` does not satisfy a bound, since its methods could not be called
  on null.
- A bound does not make `T` convert to the interface (`T` might be a struct).
- The standard library declares the primitive types' and `String`'s interfaces, each in an
  `extern struct` of the type's name that gives the type members and changes nothing else about
  it: every primitive type and `String` implements `Printable`
  ([String interpolation](13-expressions.md#expressions.interpolation)), and the numbers, `char`, and `String`
  implement `Comparable` of themselves. Their nullable types, like any nullable type, satisfy
  neither.

```java
interface Comparable<T> {
    int compareTo(T other);            // negative, zero, or positive
}

T maxOf<T extends Comparable<T>>(T a, T b) { return a.compareTo(b) >= 0 ? a : b; }

maxOf(3, 7);                // 7
maxOf("pear", "apple");     // "pear", by code point (Strings)
5.compareTo(3);             // 1: the built-in types have a compareTo outside generic code too
```

`float` and `double` compare in a total order, as Java's `Double.compare`, so sorting never
depends on where NaNs are: `-0` sorts before `0`, and NaN after every other value, including
infinity, and the same as any other NaN. This differs from the operators, under which `-0 == 0`,
and NaN is unordered and unequal even to itself.
