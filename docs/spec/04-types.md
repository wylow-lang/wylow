<a id="types"></a>
# 4. Types

Every value and every variable has a type. A type is one of the kinds in [Kinds of types](#types.kinds);
this chapter gives how each is written and what is true of its values. Chapter 5 gives which types
convert to which, and chapter 6 how variables are declared.

<a id="types.kinds"></a>
## 4.1 Kinds of types

| Kind | Types | Values |
|---|---|---|
| Primitive | `bool`, the integer types, `float`, `double`, `char` ([Primitive types](#types.primitives)) | value types |
| `String` | [`String`](#types.string) | a value type |
| Struct, enum | declared by the program or the standard library ([chapters 7 and 9](07-structs.md#structs)) | value types |
| Class, interface | declared by the program or the standard library ([chapters 8 and 10](08-classes.md#classes)) | class references |
| Fixed array | `T[N]` ([Fixed arrays](#types.fixed-arrays)) | a value type |
| Array | `T[]`, the class `Array<T>` ([Arrays](#types.arrays)) | class references |
| Span | `Span<T>`, `ReadOnlySpan<T>` ([Spans](#types.spans)) | views |
| Function | `fn(A) -> R` ([Function types](#types.functions)) | value types |
| Pointer | `T*` ([Pointer types](#types.pointers)) | values in `unsafe` code |
| Nullable | `T?` ([Nullable types](#types.nullable)) | those of `T`, or `null` |
| Weak reference | `Weak<T>` ([Weak references](17-memory.md#memory.weak)) | a value type |
| Type parameter | declared by a generic ([Type parameters](11-generics.md#generics.parameters)) | those of its type argument |

`void` is not a type of any value. It is what a function that returns nothing is declared to return.

- It is a compile-time error if `void` is written as a type, other than as the return type of a
  function, method, function type, or lambda.

**Example:**

```java
void log(String message) { ... }       // 'void' as a return type

void main() {
    List<void> nothing;                // error[WYL0062]
}
```

<a id="types.syntax"></a>
## 4.2 Writing types

```ebnf
Type          = ( TypeName | FunctionType | "(" FunctionType ")" ) { [ "?" ] TypeSuffix } [ "?" ] .
TypeName      = { Identifier "::" } ( Identifier | PrimitiveName ) [ TypeArguments ] .
TypeArguments = "<" Type { "," Type } ">" .
TypeSuffix    = "*" | "[" [ IntegerLiteral ] "]" .
PrimitiveName = "bool" | "sbyte" | "byte" | "short" | "ushort" | "int" | "uint" | "long" | "ulong"
              | "nint" | "nuint" | "char" | "float" | "double" | "void" .
```

A type name is the name of a type parameter in scope, a primitive, or a type that the file sees:
declared in its module, in the file, or imported, or reached by a path of module names
([Paths](03-programs.md#programs.paths)). `String`, `Array`, `Span`, and `ReadOnlySpan` need no
import ([Names every file sees](03-programs.md#programs.builtin-names)).

The suffixes apply from left to right. `[]` makes an array of the type so far and `[N]` a fixed
array, where `N` is an integer literal; `*` makes a pointer to it. A `?` before a suffix makes the
type so far nullable, so `T?[]` is an array of nullable elements and `T[]?` a nullable array. A final
`?` makes the whole type nullable. With several brackets, the first is the outermost: `int[4][2]` is
an array of four `int[2]`.

- It is a compile-time error if a type name is not the name of a type that the file sees
  ([Names](03-programs.md#programs.names)).
- It is a compile-time error if a type name denotes a function.
- It is a compile-time error if the type arguments of a type are not as many as its type parameters
  ([Type arguments](11-generics.md#generics.arguments)).

**Example:**

```java statements
List<Order[]> batches;          // a list of arrays
Order?[3] slots;                // three nullable orders
Order[]? pending;               // a nullable array
Widget w;                       // error[WYL0063]
memory::collectCycles c;        // error[WYL0064]
```

<a id="types.primitives"></a>
## 4.3 Primitive types

The primitive types are `bool`, `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`,
`nint`, `nuint`, `float`, `double`, and `char`. Their keywords are reserved
([Keywords](02-lexical.md#lexical.keywords)). Except for the width of `nint` and `nuint`, every
primitive type has the same size and the same values on every target.

<a id="types.primitives.integers"></a>
### 4.3.1 Integer types

| Type | Values |
|---|---|
| `sbyte` | −2^7 to 2^7 − 1 |
| `byte` | 0 to 2^8 − 1 |
| `short` | −2^15 to 2^15 − 1 |
| `ushort` | 0 to 2^16 − 1 |
| `int` | −2^31 to 2^31 − 1 |
| `uint` | 0 to 2^32 − 1 |
| `long` | −2^63 to 2^63 − 1 |
| `ulong` | 0 to 2^64 − 1 |
| `nint` | the width of a pointer, signed |
| `nuint` | the width of a pointer, unsigned |

Signed types use two's complement. The width of `nint` and `nuint` is implementation-defined
([Implementation-defined behavior](01-introduction.md#introduction.implementation-defined)); an
implementation chooses 32 or 64 bits. A conversion between `nint` or `nuint` and another type is
implicit only where it holds for both widths ([Numeric conversions](05-conversions.md#conversions.numeric)).

Every length, count, and index is an `int`: the length of an array, span, list, or string, the
index of an element, and the count of a range.

<a id="types.primitives.floats"></a>
### 4.3.2 Floating-point types

`float` and `double` are the IEEE 754 binary32 and binary64 formats. Their arithmetic is as that
standard defines it: a result too large is an infinity, and an invalid operation gives a NaN
([Arithmetic](13-expressions.md#expressions.arithmetic)). Which bits a NaN has is
implementation-defined.

<a id="types.primitives.bool"></a>
### 4.3.3 `bool`

`bool` has the two values `true` and `false`. No integer converts to `bool` or from it
([Conversions](05-conversions.md#conversions)). A condition has type `bool`.

<a id="types.primitives.char"></a>
### 4.3.4 `char`

A `char` holds a Unicode scalar value: a code point from 0 to 0x10FFFF other than a surrogate
(0xD800 to 0xDFFF). `char` is not a number. It has no arithmetic of its own, and where an operator
needs a number, the `char` is its code point as an `int`
([Operands](13-expressions.md#expressions.arithmetic.operands)). A `char` converts implicitly to the
integer types that hold every code point; an integer converts to `char` only by a cast, which
checks that the value is a scalar value ([Casts](05-conversions.md#conversions.casts)).

<a id="types.primitives.methods"></a>
### 4.3.5 Methods of primitive types

Each primitive type has a struct of its name in an extern block of the standard library
([The standard library's extern declarations](21-interop.md#interop.extern.std)). It gives the type
its members and interfaces and changes nothing else about it: `int` still names the built-in type
([The standard library](a-library.md#library.names)). Every primitive type implements `Printable`,
and every one except `bool` implements `Comparable` of itself
([`Printable`](a-library.md#library.printable), [`Comparable`](a-library.md#library.comparable)).

For an integer type `T`:

- `T.tryFrom(x)`, for an integer `x`, returns `x` as a `T?`: `x`, or `null` if `x` is not a value
  of `T`.
- `a.wrappingAdd(b)`, `a.wrappingSub(b)`, and `a.wrappingMul(b)`, for `b` of `a`'s type, return
  the result with its overflow discarded: the result modulo 2^n, for the width `n` of `T`.
- `a.checkedAdd(b)`, `a.checkedSub(b)`, and `a.checkedMul(b)`, for `b` of `a`'s type, return the
  result as a `T?`, or `null` if it overflows.

The other members are in the doc comments of the declarations.

<a id="types.nullable"></a>
## 4.4 Nullable types

No type is nullable unless it is written with `?`. For a type `T` other than those below, `T?` is
the type of the values of `T` and of `null`. For a class or interface `T`, a value of `T?` is a
class reference that may refer to nothing. For any other `T`, `T?` is a [nullable value
type](#types.nullable.values).

- It is a compile-time error if `?` is applied to a pointer type. A pointer can already be null.
- It is a compile-time error if `?` is applied to a span or another `ref struct`. Such a value
  cannot be stored in a nullable.
- It is a compile-time error to access a member of a value of a nullable type with `.`, except of a
  local variable or by-value parameter that [narrowing](16-flow.md#flow.narrowing) has proved not
  null there. A member is reached through `?.` or `!` instead
  ([Null operators](13-expressions.md#expressions.null)).

`T?` for a type parameter `T` is given in [Nullable type parameters](11-generics.md#generics.nullable),
and the reference types `ref? T` and `ref T?` in
[Nullable references](17-memory.md#memory.nullable-references).

**Example:**

```java statements
Node root = new Node();
std::print(root.next.value);    // error[WYL0065]
std::print(root.next?.value);   // the value, or null
int*? p;                        // error[WYL0066]
Span<int>? view;                // error[WYL0067]
```

<a id="types.nullable.values"></a>
### 4.4.1 Nullable value types

A nullable value type `T?`, for a type `T` that is not a class or interface, is a value type. It
holds either a value of `T` or `null`. `int?`, `Point?`, `Direction?`, `int[4]?`, `String?`, and
`(fn(int) -> int)?` are nullable value types.

A nullable value type counts as a value type for every rule of this specification. A struct does not
contain itself through one ([Recursive structs](07-structs.md#structs.recursive)).

<a id="types.fixed-arrays"></a>
## 4.5 Fixed arrays

A fixed array type `T[N]` is a sequence of exactly `N` elements of type `T`, stored in the variable
that holds it. `N` is an integer literal.

- It is a compile-time error if `N` is greater than 2^31 − 1.

A fixed array is a value: assigning it or passing it by value copies every element. Its elements are
indexed from 0 to `N` − 1 ([Indexing](13-expressions.md#expressions.indexing)), and `a.length` is
`N`. Two fixed arrays of one type are equal if their elements are equal, where the element type has
`==`.

An **array literal** `[e1, e2, ...]` makes a fixed array. Its element type is the type the context
expects for an element; without one, it is the first type of an element, or that type made
nullable, that every element converts to ([Conversions](05-conversions.md#conversions)).

- It is a compile-time error if an index that is an integer constant
  ([Integer constants](05-conversions.md#conversions.constants)) is outside 0 to `N` − 1.
- It is a compile-time error if an element of an array literal is a `void` call.
- It is a compile-time error if an empty array literal `[]` is used where no fixed array type is
  expected.
- It is a compile-time error if no type is found for an array literal's elements.

A fixed array of a type with a default value has one too: each element's default
([Default values](#types.defaults)).

**Example:**

```java statements
int[4] a = [1, 2, 3, 4];
int[2][3] grid;                 // three arrays of two ints
std::print(a[4]);               // error[WYL0068]
var blank = [log("x")];         // error[WYL0069], 'log' returns void
var empty = [];                 // error[WYL0070]
var mixed = [1, true];          // error[WYL0071]
int[3000000000] huge;           // error[WYL0072]
```

<a id="types.arrays"></a>
## 4.6 Arrays

An array type `T[]` is the class `Array<T>` of the standard library: a sequence of elements fixed
in number when it is created. Its elements are stored in the object and never move, so a reference
into an array needs only the array to stay alive. `a.length`, the number of elements, is a field
that cannot be assigned. Like any object, an array is shared when its reference is copied.

`new T[n]` creates an array of `n` elements, each the default value of `T`
([Default values](#types.defaults)); it is the call `new Array<T>(n)`. `new Array<T>(n, value)` fills
it with copies of `value`, so it is a compile-time error if `T` is not copyable
([Copyable types](#types.copyable)).

- `new T[n]` panics if `n` is negative.
- It is a compile-time error if `T` has no default value in `new T[n]` or `new Array<T>(n)`.
- It is a compile-time error if `new` creates an array of arrays, as in `new T[a][b]`.

`T?[]` is an array of nullable elements, and `T[]?` a nullable reference to an array.

**Example:**

```java statements
int[] counts = new int[n];
Order[] orders = new Order[3];          // error[WYL0078]: 'Order' is a class and has no default value
var grid = new int[2][3];               // error[WYL0073]
```

<a id="types.spans"></a>
## 4.7 Spans

`Span<T>` and `ReadOnlySpan<T>` are `ref struct`s of the standard library
([Appendix A](a-library.md#library.names)): a view of consecutive elements stored elsewhere, in an
array, a fixed array, a list, or another span. A span knows its element count (`length()`); it
has a bounds-checked indexer that returns a reference to the element (`in T` for a `ReadOnlySpan`,
so its elements can be read but not assigned), and `slice(start, length)`, which returns a span of
part of its elements.

A span is a view, and its rules are those of views and of second-class values: it can be a local
variable or a parameter, is bound where it is declared, and what it views is held or borrowed while it
is used ([`ref struct`s](07-structs.md#structs.ref), [Second-class values](17-memory.md#memory.second-class),
[Holds](17-memory.md#memory.holds), [Borrows](17-memory.md#memory.borrows)). The conversions to a span
are in [Span conversions](05-conversions.md#conversions.spans).

<a id="types.functions"></a>
## 4.8 Function types

A function type gives the parameters a function value takes and the type it returns:

```ebnf
FunctionType      = [ "shared" ] "fn" "(" [ FunctionTypeParam { "," FunctionTypeParam } ] ")" "->" Type .
FunctionTypeParam = { Annotation } [ ParameterMode ] [ "escaping" ] Type .
ParameterMode     = ( "ref" | "in" ) [ "?" ] .
```

A function type is a value type. Its parameters are always in parentheses, even none or one:
`fn() -> void`, `fn(Order) -> bool`. The mode of a parameter is part of the type, `fn(ref Point) ->
void`. The return type is always written, `void` included, and takes any suffix, so
`fn(int) -> int?` returns an `int?`. To make the function itself nullable, or an array of
functions, the type is parenthesized: `(fn(int) -> int)?`, `(fn(int) -> int)?[]`. `->` groups to
the right: `fn(int) -> fn(int) -> int` returns a function.

A function type has no parameter names and no default values. Function values have no `==`.
Whether a function value is escaping is part of its type
([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)), and so is whether it is
`shared` ([Shared functions](19-concurrency.md#concurrency.shared-functions)).

- It is a compile-time error if parentheses surround a function type that no `?`, `[`, or `*`
  follows.
- It is a compile-time error if a function type is not written with `fn`.
- It is a compile-time error if the parameters of a function type are not in parentheses.
- It is a compile-time error if the return type of a function type is a span or another `ref
  struct`. There is nothing it could view.

**Example:**

```java
void add(fn(int, int) -> int f) { }
void done((fn() -> void)? f) { }
void grouped((fn() -> void) f) { }  // error[WYL0074]
void old((int) -> int f) { }        // error[WYL0075]
void click(fn -> void f) { }        // error[WYL0076]
void view(fn() -> Span<int> f) { }  // error[WYL0077]
```

<a id="types.pointers"></a>
## 4.9 Pointer types

A pointer type `T*` is a pointer to storage that holds a `T`. A pointer type may be written
wherever a type may; only the operations on pointers need `unsafe` code
([Pointers](20-unsafe.md#unsafe.pointers)). The size of a pointer is implementation-defined
([Implementation-defined behavior](01-introduction.md#introduction.implementation-defined)). A pointer
type is not nullable, and its default value is the null pointer.

<a id="types.string"></a>
## 4.10 `String`

`String` is the type of text: an immutable sequence of UTF-8 bytes. It is a value type. Its default
value is the empty string. The standard library declares it as a struct backed by a C++ class, which gives
it its members ([Structs backed by a C++ class](21-interop.md#interop.extern.extern-structs)).

Offsets and lengths of a string count bytes, never characters: a `char` takes 1 to 4 bytes. The
methods of `String` are in the doc comments of its declaration.

- `==` and `!=` compare two strings by content: byte by byte. They are the only operators on
  strings; strings are joined by [interpolation](13-expressions.md#expressions.interpolation).
- A string has no indexer. `s.bytes()` is a `ReadOnlySpan<byte>` of its bytes, and a for-each loop
  over a string visits its characters ([For-each loops](14-statements.md#statements.for-each)). The
  span holds the string while it is used ([Holds](17-memory.md#memory.holds)).

<a id="types.defaults"></a>
## 4.11 Default values

A type is **defaultable** if it has a **default value**. These are the defaultable types and their
defaults:

| Type | Default |
|---|---|
| a numeric type | zero |
| `bool` | `false` |
| `char` | the code point 0 |
| `String` | the empty string |
| a nullable type | `null` |
| a pointer type | the null pointer |
| `Weak<T>` | a reference to nothing |
| a fixed array | each element's default, if the element type is defaultable |
| a struct | each field's initializer, or its type's default, if every field has one |
| an enum | its first variant, if that variant carries no data ([Default](09-enums.md#enums.default)) |

A type parameter, a class or interface type, a function type, and a `ref?` or `in?` type are not
defaultable: a type parameter could stand for any of them. A struct with a field of such a type,
and no initializer for it, is not defaultable; nor is a generic struct for the type arguments that
make one of its fields so.

A struct's default value is made with its parameterless constructor
([Default constructor](07-structs.md#structs.constructors.default)). A fixed array local without an
initializer holds its default instead of being unassigned
([Definite assignment](16-flow.md#flow.definite-assignment)).

- It is a compile-time error to create an array with default elements, as with `new T[n]`, of a type
  that has no default value.

<a id="types.copyable"></a>
## 4.12 Copyable types

`copyable` is a property of a type that follows from the type; no declaration states it. A value of a
copyable type is copied where a value is stored, passed by value, or returned
([Copies](07-structs.md#structs.copies)); a value of any other type is moved
([Moves](17-memory.md#memory.moves)).

A type is **copyable** unless it is:

- a type parameter without the property ([Bounds](11-generics.md#generics.bounds));
- a unique struct or unique enum ([Unique structs](07-structs.md#structs.unique));
- a fixed array, nullable type, or struct or enum not declared `unique`, with an element or field
  that is not copyable.

Class and interface types, function types, the primitive types, and `String` are copyable.
