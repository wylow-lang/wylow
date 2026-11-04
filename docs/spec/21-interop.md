<a id="interop"></a>
# 21. Interop

An extern declaration declares a function, a type, or a constant that C++ source provides. The
declaration gives the compiler what it needs to check a use, and marks where the rules of this
specification end. This chapter gives the declarations, the boundary's types, and the functions a
package exports.

<a id="interop.extern"></a>
## 21.1 Extern declarations

An **extern block** is written `extern { ... }` and preceded by `@Include("path")`, which names the
header that provides its declarations:

```java unchecked
@Include("parser.h")
extern {
    static int MAX_TOKENS;                      // a constant or macro, by name

    struct Parser;                              // opaque: usable only through pointers

    struct Range { pub int start; pub int length; }   // layout-checked

    int parser_init(uint flags);
    Parser* parser_create(int capacity);
    void parser_destroy(Parser* parser);

    @Safe ulong parser_version();
}
```

- An extern block declares functions, structs, and constants. The functions define nothing: it is a
  compile-time error for an extern function to have a body.
- A struct declared without fields is **opaque**: it is used only through a pointer to it.
- Include paths and libraries to link are given in the package manifest's `native` block
  ([The manifest](03-programs.md#programs.manifest)).
- An extern constant is read by its name, and reading it needs no unsafe code
  ([Calling extern functions](#interop.safety)).

<a id="interop.extern.extern-structs"></a>
### 21.1.1 Extern structs

An **extern struct** is a value type that the C++ class `wyl::Name` implements. `String` and
`Weak<T>` are declared this way ([The standard library](a-library.md#library.names)).

- Only the standard library declares an extern struct.
- It has no fields and no constructors. Its default value is the C++ class's default constructor.
- Its methods are all extern, and an instance method calls the member function of the same name.
- It has no `==`.

<a id="interop.extern.names"></a>
### 21.1.2 C++ names

`@CppName("name")` maps an extern declaration to any C++ name, including a namespaced or overloaded
function:

```java unchecked
extern {
    @CppName("text::parse") bool parseText(byte* text);
}
```

There is no extern declaration of a C++ template or class. A native shim declares one
([Native source files](#interop.native)).

<a id="interop.safety"></a>
## 21.2 Calling extern functions

- Calling an extern function requires `unsafe` code, unless the declaration is marked `@Safe`.
- `@Safe` is the programmer's promise that the function is safe for every argument its signature
  permits. A `@Safe` function is called from safe code as any function is. Declaring an unsafe
  function `@Safe` makes any undefined behavior that follows the programmer's responsibility.
- Reading an extern constant needs no unsafe code.
- An extern function's documentation gives, in its `# Safety` section, the conditions under which
  it may be called. Breaking one is undefined behavior
  ([Undefined behavior](20-unsafe.md#unsafe.undefined)).

<a id="interop.blittable"></a>
## 21.3 Blittable types

A type is **blittable** when its memory representation is the same in Wylow and in C++. Only
blittable types appear in an extern function's signature, in an extern constant, and in the fields
of a struct declared in an extern block.

| Wylow type | C++ type | Blittable |
|---|---|---|
| `bool` | `bool` | yes |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | `int8_t` to `uint64_t` | yes |
| `float`, `double` | `float`, `double` | yes |
| `nint`, `nuint` | `intptr_t`, `uintptr_t` | yes |
| `char` | `char32_t` | yes |
| `T*`, `void*` | `T*`, `void*` | yes |
| An enum with `@Repr` and no data | An integer type or a C enum | yes |
| A struct whose fields are all blittable | The same layout | yes |
| `T[N]` with a blittable `T` | `T[N]` | yes |
| A `@CCallback` function reference | A function pointer | yes |
| A class reference, `List<T>`, `T[]`, `Span<T>`, `ref T`, an enum with data, a nullable value, `String` | — | no |

It is a compile-time error to use a type that is not blittable where one of these places needs one.

<a id="interop.blittable.layout"></a>
### 21.3.1 Layout checks

For every struct declared with fields in an extern block, the compiler compares the size of the
declaration and the offset of each of its fields with the C++ type's. It is a compile-time error if
they differ.

<a id="interop.strings"></a>
## 21.4 Strings

A `String` is stored null-terminated. Two members of `String` cross the boundary:

- `s.cstr()` returns a `byte*` to the string's UTF-8 bytes, without copying. The pointer is valid
  while the string is alive. It is unsafe.
- `String.fromCString(byte* p)` copies a null-terminated C string into a new `String`. It panics on
  invalid UTF-8. It is unsafe.

<a id="interop.buffers"></a>
## 21.5 Buffers

`Span<T>.data()` returns a `T*` to the span's first element, and `Span<T>.byteLength()` returns the
size of its elements in bytes. `data()` is unsafe.

The pointer is valid only while the span's borrow is active, which is its lexical scope
([Borrows](17-memory.md#memory.borrows)). If foreign code keeps the pointer after that, its use is
undefined behavior.

```java unchecked
int[] values = new int[n];

unsafe {
    Span<int> view = values;
    native_write(view.data(), view.byteLength());
}
```

<a id="interop.callbacks"></a>
## 21.6 Callbacks

- Only a static or top-level function marked `@CCallback` converts to a C++ function pointer. Its
  parameters and return type must be blittable. A lambda does not convert.
- The C library's `void*` context passes the callback its state. Casting the context back to a
  Wylow type is unsafe.
- A panic inside a callback ends the program.
- A callback may run on a thread that the C++ library created. Safe code inside it reaches only
  statics, which are read-only and sendable, and the objects it creates. Reaching other data needs
  the unsafe cast of the context, and the program is responsible for avoiding races
  ([Undefined behavior](20-unsafe.md#unsafe.undefined)).

```java unchecked
@CCallback
static void onProgress(void* context, int done) { ... }

unsafe {
    native_set_progress(onProgress, null);
}
```

<a id="interop.exceptions"></a>
## 21.7 C++ exceptions

C++ exceptions do not cross the boundary. A C++ exception that would propagate into Wylow code ends
the program, and reports no location, as an abort does
([Aborts](01-introduction.md#introduction.aborts)).

A library that reports ordinary errors with exceptions is bound through a native shim that catches
them and returns an error code ([Native source files](#interop.native)).

<a id="interop.native"></a>
## 21.8 Native source files

A package may contain its own C++ source and header files. They are compiled with the program, and
their declarations are reached through an extern block like any other header.

A native shim is a small C-style layer: it is the way to bind a C++ template, class, or exception
API.

```cpp
// native/parser_shim.h
extern "C" ParserId parser_create_parser(ParserWorld* world, int capacity);
```

```java unchecked
@Include("parser_shim.h")
extern {
    struct ParserWorld;
    struct ParserId { pub uint value; }
    ParserId parser_create_parser(ParserWorld* world, int capacity);
}
```

<a id="interop.export"></a>
## 21.9 Exporting functions

`@Export` on a top-level function gives it C++ linkage and a stable name, so C++ code calls it. Its
parameters and return type must be blittable.

```java unchecked
@Export
int parser_version() { return 3; }
```
