<a id="interop"></a>
# 21. Interop

An extern declaration declares a function, a type, or a constant that C++ source provides. The
declaration gives the compiler what it needs to check a use, and marks where the rules of this
specification end. This chapter gives the declarations, the boundary's types, and the functions a
package exports.

<a id="interop.extern"></a>
## 21.1 Extern blocks

```ebnf
ExternBlock    = { Annotation } "extern" "{" { ExternMember } "}" .
ExternMember   = ExternConstant | OpaqueStruct | StructDeclaration | UnionDeclaration
               | FunctionDeclaration .
ExternConstant = { Annotation } [ Visibility ] "static" Type Identifier ";" .
OpaqueStruct   = { Annotation } [ Visibility ] "struct" Identifier ";" .
```

An **extern block** is written `extern { ... }`. It is preceded by `@Include("path")`, which names
the header that declares its members in C++:

```java unchecked
@Include("parser.h")
extern {
    pub static int MAX_TOKENS;                          // a constant or macro, by name

    pub struct Parser;                                  // opaque: usable only through pointers

    pub struct Range { pub int start; pub int length; } // the C++ struct, layout-checked

    pub int parser_init(uint flags);
    pub Parser* parser_create(int capacity);
    pub void parser_destroy(Parser* parser);

    @Safe pub ulong parser_version();
}
```

- A function or method of an extern block that is declared without a body is provided by C++. A
  method declared with a body is Wylow code, as a method of any struct is
  ([Methods](07-structs.md#structs.methods)).
- It is a compile-time error if an extern block has no `@Include`, or has more than one.
- It is a compile-time error if a function declared at the top level of an extern block has a body.
- It is a compile-time error if a struct in an extern block is declared `unique` or `ref`.
- A member of an extern block has the visibility written before it, as a declaration outside a block
  does ([Visibility](03-programs.md#programs.visibility)).
- A struct declared with a semicolon in place of its body is **opaque**. A program uses it only
  through a pointer to it, and it has no size, fields, or default value.
- Include paths, libraries to link, and the other native settings are given in the package
  manifest's `native` block ([The manifest](03-programs.md#programs.manifest)).

The identifier `union` has a meaning only where a member of an extern block declares a union.
Elsewhere it is an ordinary identifier.

<a id="interop.extern.constants"></a>
### 21.1.1 Extern constants

`static T NAME;` in an extern block declares a C++ constant, variable, or object-like macro of that
name, read as a value of type `T`.

- An extern constant is read-only. It is a compile-time error to assign it or to take its address.
- Reading an extern constant needs no unsafe code.
- It is a compile-time error if the type of an extern constant is not blittable
  ([Blittable types](#interop.blittable)).

<a id="interop.extern.c-structs"></a>
### 21.1.2 Structs and unions

A struct declared with fields in an extern block is the C++ struct of its name. The program uses the
C++ type itself, so the struct has the C++ type's layout, and each of its fields is the C++ field
of the same name ([C++ names](#interop.extern.names)).

```ebnf
UnionDeclaration = { Annotation } [ Visibility ] "union" Identifier "{" { StructMember } "}" .
```

A union declared in an extern block is the C++ union of its name. Its fields share their storage.

The rules for both:

- It is a compile-time error if a field's type is not blittable, if a field has an initializer, or if
  the struct or union declares a constructor or a destructor.
- A struct has the memberwise constructor ([The memberwise constructor](07-structs.md#structs.constructors.memberwise)).
  A union has no constructor.
- The default value of a struct or union has every field at its default value.
- A struct or union is copyable, and copying it copies its bytes.
- `==` is defined on a struct as on any struct ([Equality](07-structs.md#structs.equality)). It is
  not defined on a union.
- Writing a field of a union needs no unsafe code, and the union then holds that field. Reading a
  field of a union is unsafe ([Unsafe code](20-unsafe.md#unsafe.blocks)). The read gives the
  value whose bits are the union's bytes at that field, as `bitcast` does
  ([Pointers](20-unsafe.md#unsafe.pointers)). It is undefined behavior if those bits are not a
  valid representation of the field's type.
- A method without a body calls the C++ member function of its name. A method with a body is a
  method of the struct or union.
- It is a compile-time error to match a union with a pattern.

<a id="interop.extern.extern-structs"></a>
### 21.1.3 Structs backed by a C++ class

A struct declared in an extern block without fields, but with a body, is a value type that the C++
class of its name implements. `String` and `Weak<T>` are declared this way
([The standard library](a-library.md#library.names)).

- Only the standard library declares such a struct ([The standard library's extern declarations](#interop.extern.std)).
- It has no fields and no constructors. Its default value is the value that the C++ class's default constructor makes.
- A method without a body calls the C++ member function of the same name. A method with a body is
  a method of the struct, as in any other struct ([Methods](07-structs.md#structs.methods)).
- It has no `==`.

<a id="interop.extern.names"></a>
### 21.1.4 C++ names

A member of an extern block names the C++ declaration with its own name, unless an annotation gives
another:

- `@CppNamespace("name")` on an extern block puts the C++ names of all its members in that C++
  namespace: `@CppNamespace("text")` on a block makes its function `parse` the C++ function
  `text::parse`.
- `@CppName("name")` on a member, or on a field of a struct or union in an extern block, gives its
  whole C++ name. It overrides the block's namespace.

```java unchecked
@Include("text.h")
extern {
    @CppName("text::parse") bool parseText(byte* text);
    struct Event { @CppName("type") pub uint kind; }
}
```

There is no extern declaration of a C++ template or class outside the standard library. A native
shim binds one ([Native source files](#interop.native)).

<a id="interop.extern.std"></a>
### 21.1.5 The standard library's extern declarations

The standard library declares its own extern members in extern blocks with the rules of this
chapter. These extern blocks may also do what no other package's may:

- declare a generic function, method, or struct;
- use a type that is not blittable in a signature, including a `ref` or `in` parameter;
- declare a struct backed by a C++ class ([Structs backed by a C++ class](#interop.extern.extern-structs)),
  and declare such a struct `unique`;
- declare a struct named by a primitive type's keyword
  ([Methods of primitive types](04-types.md#types.primitives.methods));
- mark a function or method without a body `@CallerLocation`
  ([`@CallerLocation`](22-annotations.md#annotations.caller-location)).

It is a compile-time error for an extern block outside the standard library to do any of these.

<a id="interop.safety"></a>
## 21.2 Calling extern functions

- Calling an extern function requires `unsafe` code, unless the declaration is marked `@Safe`. The
  same rule applies to a method without a body of a struct or union in an extern block.
- `@Safe` is the programmer's promise that the function is safe for every argument its signature
  permits. A `@Safe` function is called from safe code as any function is. Marking an unsafe
  function `@Safe` makes the programmer responsible for any undefined behavior that follows.
- `@Pure` is the programmer's promise that the function neither panics nor changes anything a
  program can observe. It is undefined behavior if a function marked `@Pure` does either.
- An extern function's documentation gives, in its `# Safety` section, the conditions under which
  it may be called. Breaking one is undefined behavior
  ([Undefined behavior](20-unsafe.md#unsafe.undefined)).
- An extern function cannot take a variable number of arguments. A native shim wraps a C++ function
  that does ([Native source files](#interop.native)).

<a id="interop.signatures"></a>
### 21.2.1 Signature checks

The compiler compares each extern function's parameter types and return type with those of the C++
function it names. It is a compile-time error if they do not match. Two types match when:

- they are the same type;
- both are integer types, or a Wylow integer type and a C++ enumeration, of the same size and
  signedness;
- both are pointers, and their pointed-to types match, differ only in `const`, or are both one-byte
  integer or character types;
- a Wylow `@Repr` enum is a parameter, and the C++ parameter's type matches the enum's integer type.

The compiler converts an argument or a result between two types that match without being the same
type, and the conversion keeps the value's bits.

The compiler does not compare the signature of a C++ name that has more than one declaration, such
as an overloaded function. A call of such a name uses the declaration that the extern declaration's
types select.

<a id="interop.blittable"></a>
## 21.3 Blittable types

A type is **blittable** when its memory representation is the same in Wylow and in C++. Only
blittable types appear in an extern function's signature, in an extern constant, and in the fields
of a struct or union declared in an extern block.

| Wylow type | C++ type | Blittable |
|---|---|---|
| `bool` | `bool` | yes |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | `int8_t` to `uint64_t` | yes |
| `float`, `double` | `float`, `double` | yes |
| `nint`, `nuint` | `intptr_t`, `uintptr_t` | yes |
| `char` | `char32_t` | yes |
| `T*`, `void*` | `T*`, `void*` | yes |
| A struct or union declared in an extern block | The same struct or union | yes |
| A copyable struct of the program whose fields are all blittable | A struct with the same fields in the same order | yes |
| `T[N]` with a blittable `T` | `T[N]` | yes |
| A `@CCallback` function reference | A function pointer | yes |
| An enum with `@Repr` and no data | An integer type or a C++ enumeration | only as a parameter of an extern function |
| A class reference, `List<T>`, `T[]`, `Span<T>`, `ref T`, an enum with data, a nullable value, `String` | — | no |

It is a compile-time error to use a type that is not blittable in one of these places.

<a id="interop.blittable.layout"></a>
### 21.3.1 Layout checks

For every struct and union declared with fields in an extern block, the compiler compares the size
of the declaration and the offset of each of its fields with the C++ type's. It is a compile-time
error if they differ.

<a id="interop.strings"></a>
## 21.4 Strings

A `String` is stored null-terminated. Two members of `String` cross the boundary:

- `s.cstr()` returns a `byte*` to the string's UTF-8 bytes, without copying. The pointer is valid
  while the string is alive. It is unsafe, and it is undefined behavior to write through the
  pointer.
- `String.fromCString(byte* p)` copies a null-terminated C string into a new `String`. It panics on
  invalid UTF-8. It is unsafe.

<a id="interop.buffers"></a>
## 21.5 Buffers

`Span<T>.data()` and `ReadOnlySpan<T>.data()` return a `T*` to the span's first element, and
`Span<T>.byteLength()` returns the size of its elements in bytes. `data()` is unsafe. It is
undefined behavior to write through the pointer of a `ReadOnlySpan<T>`.

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

- Only a static or top-level function marked `@CCallback` converts to a C++ function pointer. A lambda
  does not convert. It is a compile-time error if its parameters or return type are not blittable.
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

A package's `native/` directory, next to its `src/` directory, holds C++ source and header files.
Each `.cpp` file in it, or in a directory below it, is compiled and linked with the program. The
`native/` directory is on the include path, so an extern block reaches its headers by their paths
from it.

A native shim is a small C-style layer: it is the way to bind a C++ template, class, exception API,
or function that takes a variable number of arguments.

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

`@Export` on a top-level function gives it C++ linkage and a stable name, so C++ code calls it. It is a
compile-time error if its parameters or return type are not blittable.

```java unchecked
@Export
int parser_version() { return 3; }
```
