# 2. Types

## 2.5 Nullability

No type is nullable unless written with `?`. This applies to every type, including value types.

```java
Texture tex = loadDefault();     // never null
Texture? override = null;        // may be null
int? cachedIndex = null;         // value + presence flag
```

| Syntax | Meaning |
|---|---|
| `if (x != null) { ... }` | Flow typing: `x` is non-null inside the block (locals only) |
| `if (x is T name) { ... }` | Pattern test that binds a new name |
| `a ?? b` | `a` if non-null, else `b` |
| `a?.b` | `null` if `a` is null, else `a.b` (result is nullable) |
| `a!` | `a`, or panic if null |

### Rules

- **Members of a nullable value** can only be reached through `?.` or `!`, or through a narrowed
  local. `a.b` on any other nullable expression is an error.
- **Narrowing** (flow typing) applies to locals and by-value parameters only, since nothing else can
  change them behind the code's back. A `ref` or `in` parameter or variable is not narrowed: another
  reference to the same variable could set it to null after the check. A local is non-null where
  flow analysis proves it:
  - in the branch where `x != null` or `x == null` is known, including after `&&`, `||`, `!`, and
    `?:`, and after an `if` whose other branch cannot complete (`if (x == null) { return; }`);
  - after assigning it a non-null value, until it is assigned something nullable;
  - in a loop, only if the loop does not assign it (each iteration starts without narrowing the loop
    could undo), or after a condition in the loop narrows it again: `while (x != null) { x = x.next; }`.
  - passing it by `ref` ends its narrowing, since the callee may assign it null.
- `x is T name` binds `name` (non-null, type `T`) where the test succeeded. It also narrows `x`.
  A name bound in an `if` or `while` condition is in scope for the whole statement, but definitely
  assigned only where the test is known to have succeeded.
- `a?.b` chains like Kotlin: `a?.b.c` is an error when `a?.b` is nullable; write `a?.b?.c`. The
  result is `b`'s type made nullable (`void` for a call returning nothing), so `node?.count` is an
  `int?`. When `a` is null, a call's arguments are not evaluated.
- `a ?? b` requires a nullable `a`. Its type is `a`'s non-null type (or a supertype both convert to)
  when `b` is non-null, otherwise nullable. `b` is evaluated only when `a` is null.
- `!` and `?.` on a value that is not nullable are errors, so that redundant checks don't accumulate.
- `null` converts to any nullable type; `var x = null;` is an error. Any reference and any nullable
  value can be compared with `null`.
- Overloads cannot differ only in nullability (`f(Player)` and `f(Player?)`): for references both
  are the same type at runtime, and in generic code `f(T)` and `f(T?)` could be too.
- A `ref` to a nullable variable is `ref T?`; a reference that may refer to nothing is `ref? T`
  ([03-memory-model.md](03-memory-model.md#nullable-references-ref-t)).

### Nullable value types

`int?`, `Vec3?`, `Direction?`, and `int[4]?` are values like the type they make nullable, stored
inline with a presence flag. Any type
can be made nullable except a pointer, which can already be null, and a `ref struct`, which can't be
stored. Nullable values follow the rules above, and also:

- **Conversions**: a value converts implicitly to a nullable of any type it converts to (`int` to
  `long?`, an integer constant to `float?`), and a nullable value to a nullable of a wider type
  (`int?` to `long?`), which stays null if it was null.
- **Default**: a nullable type's default value is `null`, so it is always defaultable (§2.13).
- **Operators**: `==` and `!=` compare a nullable value with `null`, with another nullable value,
  or with a value, when the value type supports `==`: both sides become nullables of the type the
  values compare as, and `null` equals only `null`. No other operator accepts a nullable value (no
  lifted `+` or `<` as in C#): narrow it first, or use `!` or `??`. Where an operator, a condition,
  a `switch`, or `++` needs a value, a nullable local is narrowed as for member access, and flow
  analysis checks that it was checked.
- `x is int n` tests that `x` is not null and binds its value. The type must be exactly the value
  type: an `int?` can never be a `long`.
- `print` accepts the nullable primitive types and prints `null` for null.
- **Changing it in place**: a narrowed `mut` local's value is a variable, so `if (v != null) {
  v.x = 1; v.move(); }` changes `v`. Nothing may keep referring into that value, since assigning `v` null
  destroys it: a `ref` or `in` variable, a span, or a `for` loop over its elements is an error
  (copy the value to a variable first), as is passing `v` by `ref` in the same call or assignment
  that uses its value by reference (`v.moveWith(ref v)`), or passing it a lambda that assigns `v`
  (`apply(v.x, () -> { v = null; })`, with an `in` parameter). A lambda made earlier ends the
  narrowing where it is made, and one made later can't run before then. A `ref` variable can't refer to a nullable
  local itself either, since the local could then change after a null check. A narrowed local that
  isn't `mut` is never assigned again, so `in` variables, spans, and loops can refer into its value.
- `v!` and `v?.m()` work on a copy: `v!.x = 1` and `v!.m()` are errors, as for any temporary
  ([06-syntax.md](07-structs.md#structs.methods.mut)), and so is `v?.m()`, when `m` is a `mut` struct
  method, since its changes would be lost.
- A struct cannot contain itself through a nullable (`struct Link { pub Link? next; }`), since the
  value would be stored inline; use a class.

## 2.7 Mutability and constants

Variables, parameters, class fields, and struct methods are **immutable unless declared `mut`**,
as in Rust. Most never change, so the marker goes on the exception.

```java
int lives = 3;                         // can't be assigned again
mut int score = 0;
score += 10;
mut var target = enemies.first();
```

- **A variable that isn't `mut` always holds the same value, but an object it refers to can still
  change.** `List<int> list = new List<int>(); list.add(1);` is fine; `list = other;` is not.
  Whether an object's own fields can change depends on their declarations (below).
- **A value follows its variable.** A struct, enum, or fixed array is stored in its variable, so when
  the variable isn't `mut`, nothing in it can change. Its fields and elements can't be assigned, it
  can't be passed by `ref` or bound to a `ref` variable, its `mut` methods can't be called,
  and a fixed array converts only to `ReadOnlySpan`. These are the rules of an `in` parameter
  ([06-syntax.md](12-functions.md#functions.parameters.modes)). For the same reason, struct fields take no `mut`.
- **Class fields** are immutable unless declared `mut`. The class's own constructors may assign
  them through `this` any number of times; nothing else can: not a method, not a subclass's
  constructor, and not a lambda created in a constructor. A struct stored in a field follows the
  field, so `body.position.x = 1` needs `position` to be `mut`.
- **A variable declared without a value is assigned once, later.** Assigning it where it might
  already have been assigned, including in a loop or a lambda, is an error unless it is `mut`:

  ```java
  String label;
  if (hp > 0) {
      label = "alive";
  } else {
      label = "dead";
  }
  ```
- **`mut` goes first**, after any annotations and modifiers: `mut int count;`, `pub mut int frames;`,
  `mut escaping fn() -> void onDone`, `void aim(mut Vec3 dir)`, `for (mut int i = 0; i < n; i++)`, `for (mut Vec3 p : points)`,
  `(mut x) -> ...`. It is not allowed on:
  - `ref` and `in` parameters and variables, which always refer to the same place. Through a `ref`,
    the place can be changed, since it had to be mutable to be referred to;
  - non-escaping function parameters and variables, which can't be reassigned (§6.7);
  - struct fields and variant fields, which follow their value;
  - pattern bindings, which are copies (or `in` and `ref` references, which refer to their place);
    copy one to a `mut` variable to change it.

  A `ref struct` variable may be `mut`, which allows calling its `mut` methods, but it
  still can't be reassigned ([03-memory-model.md](03-memory-model.md#spans)).
- **A struct method can't change `this` unless it is `mut`**: `mut void normalize()`. Only `mut`
  methods need a mutable place to be called on ([06-syntax.md](07-structs.md#structs.methods.mut)).
- `mut` is not part of a function's signature: an override may declare its parameters `mut` or not
  regardless of the method it overrides.
- `const` declares a compile-time constant: `const float PI = 3.14159;`.


## 2.9 Structs

### Unique structs

A `unique struct` can't be copied, only moved, so each value has exactly one owner:

```java
unique struct SampleBuffer {
    pub Vec<float> samples;            // a unique growable array (proposed, see below)
    pub int rate;
}

SampleBuffer take = record(mic);
SampleBuffer kept = take;              // moves: 'take' can't be used after this
mixer.queue(kept);                     // a by-value parameter moves too
print(kept.rate);                      // error: 'kept' is used after it is moved
```

- Every place a struct would be copied moves a unique one instead: initializing or assigning a
  variable, passing a by-value argument, returning, storing into a field or element, and an
  escaping lambda's capture ([03-memory-model.md](03-memory-model.md#moves)). Using a value after it
  is moved is a compile error. `in` and `ref` parameters, `in` and `ref` variables, and method calls
  refer to it in place, as for any struct, and never move it.
- A struct with a field of a unique type must itself be declared `unique`, and so must an enum with
  a variant field of one (`unique enum`), so whether a type copies is read where it is declared.
  A fixed array `T[N]` and a nullable `T?` of a unique `T` are unique, and so is a generic struct
  or enum given a type argument that makes one of its fields unique: `Pair<SampleBuffer, int>`.
  Classes are never unique: an object is shared by reference counting.
- A unique type isn't `copyable` (§2.13, Type properties), so generic code copies a `T` only where
  its bound says it may.
- A unique struct may declare a destructor, `~Name() { ... }`, with no parameters and no modifiers.
  It is a compile-time error to declare one on a struct that isn't `unique`. A unique enum has no
  destructor, but releasing it releases its variant's fields, which runs the destructors of the
  fields that have one.
- To copy one on purpose, give it a method that makes the copy, conventionally `clone()`.

#### Destructors of unique structs

A unique struct's destructor runs when a value of the type is released
([03-memory-model.md](03-memory-model.md#when-references-are-released)): when its owner goes out of
scope while it holds the value, when it is overwritten, when an element holding it is removed or its
container is destroyed, and when the value containing it is released. It does not run for a value
that has been moved.

- The destructor's body runs first. The struct's fields are then released in reverse declaration
  order, as a class's are. A field whose type has a destructor runs it.
- In the body, `this` is a `ref`, as in a `mut` method, so the body may assign to fields and call
  `mut` methods. The value is in use for the whole body
  ([03-memory-model.md](03-memory-model.md#values-in-use)), so replacing or releasing the whole
  value there panics. It is a compile-time error to move out of `this`.
- A panic in a destructor ends the program like any other panic.
- A static can't have a unique type (§5.5), so a destructor never runs at the end of the program
  for a static's value.

**`discard`.** `discard name;` ends the lifetime of a by-value parameter without running its
destructor's body; its fields are still released.

- It is a compile-time error if `name` is not a by-value parameter of a unique struct type that has
  a destructor, or if the function is not declared in the same file as the type. A function
  declared there can be a free function, a static method, or an extension method whose receiver is
  `this T` for that type.
- After `discard name;` the parameter is moved: using it is a compile-time error
  ([03-memory-model.md](03-memory-model.md#moves)). Paths that don't discard it release it when
  the function ends, running the destructor, so a function that closes a resource by itself can
  return an error before discarding.

```java
unique struct File {
    int descriptor;
    ~File() { sys::close(descriptor); }
}

Result<void, IoError> close(File f) {
    if (sys::flush(f.descriptor) != 0) {
        return Result.err(IoError.Flush);       // 'f' is released here: the destructor closes it
    }
    var status = sys::close(f.descriptor);
    discard f;                                  // already closed: don't run the destructor
    return status == 0 ? Result.ok() : Result.err(IoError.Close);
}
```

## 2.10 Classes

### Construction

Construction follows Java's order, and C++'s rule for what the object is while it is built:

0. A constructor whose first statement is `this(...)` calls another constructor of its class,
   which runs steps 1 to 3 (or chains again), and then runs its own body. It runs neither a base
   constructor nor the field initializers itself, so each runs once, as in Java and C#. Only one of
   `this(...)` and `super(...)` can be written, as the first statement. The arguments of
   `this(...)` cannot use `this`, and follow the same rule for references and views as
   `super(...)`'s, for the same reason. A constructor whose chain of `this(...)` calls comes back
   to it is an error ("recursive constructor invocation", as in Java), however long the chain.
1. The base class constructor runs first. `super(...)` must be the first statement of the
   constructor; without it, the base's parameterless constructor is called. Its arguments cannot use
   `this`.

2. Field initializers run in declaration order. They cannot use `this`.
3. The constructor body runs.

A field *needs a value* if it has no initializer and its type has no default (a non-null reference,
a type parameter, or a struct containing one). In the body, **until every such field of the class
is assigned, `this` can only be used to assign fields and to read fields already assigned**: no
method calls on it, no passing it anywhere. Every such field must be assigned before the
constructor returns, on every path. Flow analysis checks both. The same rule applies to struct
constructors. After `this(...)` every field is assigned, since the constructor it called had to
assign them, and so is every field of a struct constructor after it assigns `this` as a whole.

**While a base class's constructor or destructor runs, the object is an instance of that base
class**, as in C++: virtual calls dispatch to the base class's implementations, so they can never
see the derived class's fields before they are assigned or after they are destroyed. Calling an
abstract method at that point panics.

### Strings

`String` is an immutable, UTF-8, reference-counted **built-in value type**: its bytes live in one
counted block that its copies share (it may cross threads; see
[05-concurrency.md](05-concurrency.md)). The standard library declares it as an
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
  null-terminated ([07-interop.md](07-interop.md#strings)).
- `indexOf` returns the byte offset of the first occurrence, which is where `slice` can start.
  An empty string occurs at offset 0, and every string contains it.
- `compareTo` orders by byte, which for UTF-8 is the order of the code points. It doesn't apply
  locale rules or Unicode normalization: `"é"` written as one code point and as `e` plus a
  combining accent are different strings.
- `String` has no indexer, since neither a byte nor a code point is what `s[i]` would obviously
  mean. `s.bytes()` is a `ReadOnlySpan<byte>` of the UTF-8 bytes, without a copy: `s.bytes()[0]`,
  `for (byte b : s.bytes())`. Like any span (§2.11) it holds what it views, here the string, so the
  bytes stay valid even if the variable it came from is reassigned. A method can't return a view
  of a string field, which could be replaced while the caller uses it, as for an object field.
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
  borrows the list ([03-memory-model.md](03-memory-model.md#33-references)).

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
  ([03-memory-model.md](03-memory-model.md#borrow-counters-on-containers)).
- `count()` is the number of elements. `first()` and `last()` are copies of the first and last
  elements, and `removeLast()` removes and returns the last one, as a stack's pop; all three are
  null when the list is empty.

### Spans

`Span<T>` and `ReadOnlySpan<T>` are standard library `ref struct`s
([03-memory-model.md](03-memory-model.md#spans)): a pointer and a length, with a bounds-checked
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
  ([06-syntax.md](13-expressions.md#expressions.interpolation)), and the numbers, `char`, and `String`
  implement `Comparable` of themselves. Their nullable types, like any nullable type, satisfy
  neither.

```java
interface Comparable<T> {
    int compareTo(T other);            // negative, zero, or positive
}

T maxOf<T extends Comparable<T>>(T a, T b) { return a.compareTo(b) >= 0 ? a : b; }

maxOf(3, 7);                // 7
maxOf("pear", "apple");     // "pear", by code point (§2.10, Strings)
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
  enum (§2.9), or a fixed array, nullable type, or struct or enum not declared `unique` with an
  element or field that isn't copyable. Class and interface references, function types, primitives,
  and `String` are copyable.
- A type is **sendable** as [05-concurrency.md §5.2](05-concurrency.md#52-sendable-types) says,
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
