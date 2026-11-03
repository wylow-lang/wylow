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

## 2.11 Arrays and spans

| Type | Lives | Size | Kind |
|---|---|---|---|
| `T[N]` | Inline (stack, struct, class) | Compile-time, fixed | Value |
| `T[]` | Heap | Set at creation, never changes | Class (refcounted) |
| `List<T>` | Heap | Growable | Class (refcounted) |
| `Span<T>` / `ReadOnlySpan<T>` | — (a view) | Whatever it views | Second-class reference |

```java
float[16] matrix;                              // inline
float[] heights = new float[width * height];   // heap, length fixed, default values
List<Enemy> spawned = new List<Enemy>();       // heap, growable
```

- All indexing is bounds-checked, in `unsafe` code too (`getUnchecked(i)` skips it, [Unchecked operations](20-unsafe.md#unsafe.unchecked)); a failed check panics with the
  index and the length.
- A `T[]` **never reallocates**: its length never changes and its elements never move, so
  references into it only need to keep the array alive.
- `T[N]`, `T[]`, and `List<T>` convert **implicitly** to `Span<T>` and `ReadOnlySpan<T>`.
- `Span<T>` follows the same rules as `ref` (cannot be a field, cannot be captured by an escaping
  closure, and can be returned only by a method viewing its receiver). Taking a span of a `List`
  borrows the list ([Borrows](17-memory.md#memory.borrows)).

```java
float sum(ReadOnlySpan<float> values) { ... }

sum(matrix);    // T[N]
sum(heights);   // T[]
sum(spawned);   // List (borrowed for the duration of the call)
```

### Fixed arrays `T[N]`

- `N` is an integer literal. Like C, the first length is the outer one: `int[4][2]` is four
  `int[2]`s, indexed `a[i][j]` with `i < 4`.
- A fixed array is a value: assignment and passing by value copy every element. `==` compares
  the elements when their type supports `==`.
- `a.length` is `N`. An index that is a constant outside `0..N` is a compile-time error.
- Array literals create fixed arrays: `int[3] a = [1, 2, 3];`. The element type is the expected
  one, or else the first element type that every element converts to. An empty literal `[]`
  needs an expected type.
- A fixed array's default value has each element's default. Unlike other locals, a fixed-array
  local without an initializer holds that default rather than being unassigned, since writing out
  a literal of every element is impractical; this requires the element type to have a default.

### Heap arrays `T[]`

- `T[]` is the standard library class `Array<T>`, and `new T[n]` calls its constructor
  `Array(int length)`, which fills the array with default values. Only the compiler can check
  that `T` has one, so `new T[n]`, and `new Array<T>(n)` written out, are errors for a `T`
  without a default, such as a non-null class type. `new Array<T>(n, value)` fills it with copies
  of `value` instead.
- `T?[]` is an array of nullable elements; `T[]?` is a nullable reference to an array.
- `a.length` is a field, which can't be assigned. `new T[n]` panics if `n` is negative.
- Like any object, copying a `T[]` shares it.

### Lists `List<T>`

- `List<T>` is a standard library class: a pointer to its elements, its count, and its capacity.
  Adding past the capacity moves the elements to new storage, 4 elements at first and then twice
  the old capacity.
- The capacity never shrinks by itself: `clear` and `removeAt` keep the storage for reuse, which
  suits lists refilled every frame. `reserve(n)` grows the capacity to at least `n`, and `trim()`
  shrinks it to exactly `count()`, freeing the storage entirely when the list is empty. Unlike C++'s
  `shrink_to_fit`, `trim` always does so.
- While a list is borrowed, changing its length or capacity panics
  ([Borrows](17-memory.md#memory.borrows)).
- `count()` is the number of elements. `first()` and `last()` are copies of the first and last
  elements, and `removeLast()` removes and returns the last one, as a stack's pop; all three are
  null when the list is empty.

### Spans

`Span<T>` and `ReadOnlySpan<T>` are standard library `ref struct`s
([`ref struct`s](07-structs.md#structs.ref)): a pointer and a length, with a bounds-checked
indexer, `length()`, and `slice(start, length)`. A `ReadOnlySpan`'s indexer returns `in T`, so its
elements can be read but not changed.

Conversions to spans are implicit, from:

- a fixed array stored in a variable, field, or element. A temporary fixed array, such as one a
  function returned, has no storage to view. A read-only fixed array (an `in` parameter, or a
  field in a struct method that isn't `mut`) converts only to `ReadOnlySpan`.
- a value of a type implementing the standard library's `Spannable<T>`, by calling its `asSpan()`,
  or `ReadOnlySpannable<T>`, by calling `asReadOnlySpan()`. `Array`, `List`, and `Span` implement
  them, and so can user types.

A generic function taking a span infers its element type from any of these:
`T first<T>(ReadOnlySpan<T> values)` accepts an `int[4]` or a `List<int>`.

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

### Type properties

`copyable` and `sendable` are properties of a type that follow from the type itself; no
declaration states them. A bound may name them: `T extends Comparable<T> & copyable` requires a
type argument that implements `Comparable<T>` and is copyable.

- A type parameter has a property only if one of its bounds names it.
- A type is **copyable** unless it is a type parameter without the property, a unique struct or
  enum ([Unique structs](07-structs.md#structs.unique)), or a fixed array, nullable type, or
  struct or enum not declared `unique` with an element or field that isn't copyable. Class and
  interface references, function types, primitives, and `String` are copyable.
- A type is **sendable** as [Sendable types](19-concurrency.md#concurrency.sendable) says,
  with a type parameter sendable only if a bound names the property.
- `copyable` and `sendable` have this meaning only in a bound. Elsewhere they are ordinary
  identifiers.

```java
struct Pair<A, B> {
    pub A first;
    pub B second;
}

Pair<T, T> twin<T extends copyable>(T value) {
    return Pair<T, T>(first: value, second: value);     // valid: the first use copies 'value'
}

Pair<T, T> twinOf<T>(T value) {
    return Pair<T, T>(first: value, second: value);     // error: 'value' is used after it is moved
}
```
