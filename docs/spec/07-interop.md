# 7. Interop

VPL compiles to C++, so interop needs no foreign function interface in the usual sense: no calling
conventions, ABI handling, or dynamic loading. The generated C++ includes the relevant header and
calls foreign functions by name. Interop in VPL is about two things:

1. **Declaring** foreign functions, types, and constants so the VPL compiler can type-check them.
2. **Marking** where VPL's safety guarantees end.

## 7.1 `extern` blocks

```java
package sdl;

@Include("SDL3/SDL.h")
extern {
    const uint SDL_INIT_VIDEO;                      // constant or macro, emitted by name

    struct SDL_Window;                              // opaque: usable only through pointers

    struct SDL_Rect { int x; int y; int w; int h; } // layout-checked (§7.4)

    int  SDL_Init(uint flags);
    SDL_Window* SDL_CreateWindow(byte* title, int w, int h, ulong flags);
    void SDL_DestroyWindow(SDL_Window* window);

    @Safe ulong SDL_GetTicks();
}
```

- An `extern { }` block may contain function declarations, struct declarations (opaque or with
  fields), and constants.
- The compiler generates **no code** for extern declarations. It emits `#include` for each
  `@Include` and refers to the names directly.
- `@Include("path")` names the header that provides the declarations. Include paths and libraries
  to link are configured in the project's build file (*open*, see compiler design).

> Implemented so far: only the standard library may declare extern functions, one at a time:
> `static extern void check(int index, int length);` in a struct, or a free `extern` function.
> The C++ runtime (`runtime/vpl/intrinsics.h`) implements them, as `vpl::name`, or `vpl::type::name` for a
> method of a type (the type's name in lower camel case). Extern blocks and `@Include` are not
> implemented.
>
> The standard library may also declare an `extern struct`, a value type implemented by the C++
> class `vpl::Name` (`Weak<T>` is `vpl::Weak`). It has no fields or constructors, its default value
> is the class's default constructor, it doesn't support `==`, and its methods are all `extern`:
> instance methods call the C++ class's member function of the same name.
### C++ names

Since the output is C++ source, a declaration can map to any C++ name, including namespaced and
overloaded functions:

```java
extern {
    @CppName("ImGui::Begin") bool imguiBegin(byte* name);
}
```

C++ templates and C++ classes cannot be declared directly. Use a native shim (§7.8).

## 7.2 Safety: extern calls are `unsafe`

> **Calling an extern function requires `unsafe`**, unless the declaration is marked `@Safe`.

Foreign code can do anything (write through invalid pointers, retain pointers, free memory still in
use) and the compiler cannot check it.

`@Safe` is the **programmer's promise** that the function is safe for every argument its signature
permits (for example `SDL_GetTicks()` or `sinf(float)`). A `@Safe` function may be called from safe
code. Declaring an unsafe function `@Safe` makes any resulting UB the programmer's responsibility.

Reading an extern constant does not require `unsafe`.

## 7.3 The wrapper pattern

The intended structure is two layers:

1. A **binding package** containing raw `extern` declarations.
2. A **safe wrapper** in VPL that owns foreign resources in classes, converts errors to `Result`,
   and exposes only safe types.

Game code uses only the wrapper.

```java
package game.platform;

import sdl.*;

public class Window {
    private SDL_Window* handle;

    private Window(SDL_Window* h) { handle = h; }

    public static Result<Window, String> create(String title, int w, int h) {
        unsafe {
            var p = SDL_CreateWindow(title.cstr, w, h, 0);
            if (p == null) return Err(String.fromCString(SDL_GetError()));
            return Ok(new Window(p));
        }
    }

    ~Window() {
        unsafe { SDL_DestroyWindow(handle); }
    }
}
```

A class destructor (`~ClassName()`) runs when the last reference is released (see
[03-memory-model.md](03-memory-model.md#32-objects-classes)).

## 7.4 Blittable types

Only **blittable** types (types with an identical memory representation in C) may appear in extern
signatures and extern struct fields. This is checked at compile time.

| VPL type | C type | Blittable |
|---|---|---|
| `bool` | `bool` | ✅ |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | `int8_t` ... `uint64_t` | ✅ |
| `float`, `double` | `float`, `double` | ✅ |
| `nint`, `nuint` | `intptr_t`, `uintptr_t` | ✅ |
| `char` | `char32_t` | ✅ |
| `T*`, `void*` | `T*`, `void*` | ✅ |
| Enums with `@Repr` and no data (§7.5) | Integer / C enum | ✅ |
| Structs whose fields are all blittable | Same layout | ✅ |
| `T[N]` with blittable `T` | `T[N]` | ✅ |
| `@CCallback` function pointers (§7.6) | Function pointer | ✅ |
| Class references, `String`, `List<T>`, `T[]`, `Span<T>`, `ref T`, enums with data, nullable values | — | ❌ |

### Layout verification

For each extern struct declared with fields, the compiler emits size and offset checks into the
generated C++:

```cpp
static_assert(sizeof(SDL_Rect) == 16);
static_assert(offsetof(SDL_Rect, x) == 0);
static_assert(offsetof(SDL_Rect, w) == 8);
// ...
```

If the VPL declaration does not match the real header, **the build fails**.

## 7.5 Enums with integer representations

```java
@Repr(uint)
enum Format {
    Undefined = 0,
    R8G8B8A8_UNORM = 37,
}

@Flags
@Repr(ulong)
enum WindowFlags {
    Fullscreen = 0x1,
    Resizable  = 0x20,
}

var f = WindowFlags.Fullscreen | WindowFlags.Resizable;
```

- `@Repr(T)` gives the enum an explicit integer representation `T`, as Rust's `#[repr(u32)]`.
  It is an annotation like the struct layout ones (§2.9), not a supertype: the enum doesn't
  convert to `T` implicitly. Variants may have explicit values.
- `@Flags` enums support `|`, `&`, `^`, `~` and `has(flag)`. Any combination of bits is a valid value.
- Converting an integer to a non-`@Flags` enum with a cast (`(Format)i`) **panics** if the value is
  not a declared variant. `Format.tryFrom(i)` returns `Format?`. In `unsafe` code, `bitcast<Format>(i)`
  performs no check.
- A variant without a value is one more than the previous variant, and the first is `0`. Two
  variants may not have the same value. Values must be integer literals that fit the enum's
  integer type, which is `int` when values are given without `@Repr`.
- Only enums whose variants carry no data can have `@Repr` or values.

> Implemented so far: `@Repr`, values, and casts both ways. `@Flags`, `tryFrom`, and `bitcast` are
> not implemented.

Enum variant names keep their C spelling when bound from C (`R8G8B8A8_UNORM`); the naming-convention
warning does not apply inside `extern` blocks or to enums with `@Repr`.

## 7.6 Passing data across the boundary

### Strings

- `String` is always stored **null-terminated** internally.
- `s.cstr` (unsafe) returns a `byte*` to the string's UTF-8 bytes without copying. It is valid while
  the string is alive.
- `String.fromCString(byte* p)` (unsafe) copies a null-terminated C string into a new `String`. It
  panics on invalid UTF-8.

### Buffers

`Span<T>.data` (unsafe) returns a `T*` to the span's first element; `byteLength()` gives the size in bytes.

```java
unsafe {
    Span<Vertex> verts = mesh.vertices;
    vkCmdUpdateBuffer(cmd, buffer, 0, verts.byteLength(), verts.data);
}
```

The pointer is valid **only while the span's borrow is active** (its lexical scope). If foreign code
retains the pointer beyond that, behavior is undefined.

### Callbacks

```java
@CCallback
static void onAudio(void* userdata, byte* stream, int len) { ... }

unsafe { SDL_SetAudioCallback(device, onAudio, null); }
```

- Only **static or top-level functions** marked `@CCallback` can be converted to C function pointers.
  Their signatures must be blittable. Lambdas cannot be converted (they may have captures).
- Context is passed with the C library's `void* userdata` mechanism. Casting `userdata` back to a
  VPL type is `unsafe`.
- A **panic** inside a callback aborts the process. (VPL never unwinds, so there is no unwinding
  through C frames.)
- A callback may run on a thread created by the foreign library. Safe code inside it can only reach
  that thread's statics; reaching other data requires the `unsafe` userdata cast, and the
  programmer is responsible for avoiding races.

## 7.7 C++ exceptions

VPL has no exceptions. Extern calls are emitted through `noexcept` wrappers, so a C++ exception that
would propagate into VPL code calls `std::terminate`, aborting the process. Libraries that use
exceptions for ordinary errors need a native shim (§7.8) that catches them and returns error codes.

## 7.8 Native C++ files

A project may include its own `.cpp` and `.h` files. They are compiled alongside the generated C++
and can be bound with `extern` like any other header.

This is the intended route for template-heavy C++ libraries (physics engines, Dear ImGui, memory
allocators): write a small C-style shim and bind the shim.

```cpp
// native/physics_shim.h
extern "C" BodyId physics_create_box(PhysicsWorld* w, float hx, float hy, float hz);
```
```java
@Include("physics_shim.h")
extern {
    struct PhysicsWorld;
    struct BodyId { uint value; }
    BodyId physics_create_box(PhysicsWorld* w, float hx, float hy, float hz);
}
```

## 7.9 Exporting VPL functions

`@Export` on a top-level function gives it C linkage and a stable, unmangled name so C/C++ code can
call it. Its signature must be blittable.

```java
@Export
int vpl_plugin_version() { return 3; }
```

## 7.10 Binding generation (tooling)

Hand-writing large bindings is impractical. Planned tooling:

- **`vpl bindgen <header>`** parses a C header with libclang and emits a VPL file of `extern`
  declarations.
- **Vulkan** bindings are generated from the official `vk.xml` registry.

Every C construct maps directly onto an `extern` construct, so generated bindings need no special
language support.
