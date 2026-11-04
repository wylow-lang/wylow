<a id="annotations"></a>
# 22. Annotations

An annotation marks a declaration with a setting that changes how it is treated, not what it is.

<a id="annotations.syntax"></a>
## 22.1 Annotation syntax

```ebnf
Annotation         = "@" Identifier [ "(" AnnotationArgument { "," AnnotationArgument } ")" ] .
AnnotationArgument = Type | Literal .
```

A declaration may have annotations before its visibility and its other modifiers:
`@CallerLocation pub static void check(...)` ([Visibility](03-programs.md#programs.visibility)). An
annotation is `@Name` or `@Name(arguments)`. An argument is a type, as in `@Repr(uint)`, or a
literal, as in `@Align(16)`.

Parentheses right after an annotation are its arguments, unless they begin with `fn`: they are then
the parentheses around the annotated declaration's function type, as in
`@AllowLargeStack (fn(int) -> int)? handler;`.

- It is a compile-time error to give an annotation arguments it does not take.
- **An annotation never changes a type**: what can be assigned, converted, or captured. Whatever is
  part of a type is a keyword, such as a parameter's mode (`ref`, `in`) or whether a function is
  `escaping` ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)).

<a id="annotations.list"></a>
## 22.2 The annotations

These are the annotations:

| Annotation | Used on | Meaning | Rules |
|---|---|---|---|
| `@CallerLocation` | a function, method, constructor, or indexer | A panic inside it reports where it was called from | [`@CallerLocation`](#annotations.caller-location) |
| `@AllowLargeStack` | a local variable | Suppresses the warning for a large local | [`@AllowLargeStack`](#annotations.allow-large-stack) |
| `@Align(n)` | a struct or field | Aligns it to `n` bytes, a power of two | [Layout](07-structs.md#structs.layout) |
| `@Packed` | a struct | No padding between its fields | [Layout](07-structs.md#structs.layout) |
| `@Repr(T)` | an enum | Its integer type | [Integer representations](09-enums.md#enums.repr) |
| `@Flags` | an enum | Its values combine as bits | [Flags](09-enums.md#enums.flags) |
| `@Acyclic` | a class | Its objects never form cycles | [What the collector looks at](17-memory.md#memory.cycles.collected) |
| `@Include("path")` | an extern block | The header that provides its declarations | [Extern declarations](21-interop.md#interop.extern) |
| `@Safe` | an extern function | The function may be called from safe code | [Calling extern functions](21-interop.md#interop.safety) |
| `@CppName("name")` | an extern declaration | The C++ name it declares | [C++ names](21-interop.md#interop.extern.names) |
| `@CCallback` | a static or top-level function | Its reference converts to a C++ function pointer | [Callbacks](21-interop.md#interop.callbacks) |
| `@Export` | a top-level function | It has C++ linkage and a stable name | [Exporting functions](21-interop.md#interop.export) |

- It is a compile-time error to use a name that is not one of these annotations.
- It is a compile-time error to use an annotation on a declaration that its row does not name.

<a id="annotations.caller-location"></a>
## 22.3 `@CallerLocation`

Every panic inside a function marked `@CallerLocation`, including those of the checks it calls and
of other `@CallerLocation` functions it calls, reports the location of the call to it. The standard
library uses it so that a failed `list[i]` reports the program's line rather than one inside `List`
([Panics](18-errors.md#errors.panics)).

- A lambda written in such a function reports its own locations, since it may run after the call has
  returned.
- `@CallerLocation` applies to functions, methods, constructors, and indexers.
- It is a compile-time error to mark a virtual, abstract, or interface method `@CallerLocation`.

**Example:**

```java
@CallerLocation
int at(int[] values, int index) {
    if (index < 0 || index >= values.length) {
        panic("index out of bounds");
    }
    return values[index];
}

void main() {
    var values = new int[2];
    print(at(values, 3));          // a panic here names this line
}
```

<a id="annotations.allow-large-stack"></a>
## 22.4 `@AllowLargeStack`

`@AllowLargeStack` on a local variable's declaration suppresses the warning an implementation may
give for a local whose value is large. It has no other effect.
