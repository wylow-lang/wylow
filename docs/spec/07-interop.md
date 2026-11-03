# 7. Interop

Wylow compiles to C++, so interop needs no foreign function interface in the usual sense: no calling
conventions, ABI handling, or dynamic loading. The generated C++ includes the relevant header and
calls foreign functions by name. Interop in Wylow is about two things:

1. **Declaring** foreign functions, types, and constants so the Wylow compiler can type-check them.
2. **Marking** where Wylow's safety guarantees end.

## 7.1 `extern` blocks

```java
// module sdl

@Include("SDL3/SDL.h")
extern {
    const uint SDL_INIT_VIDEO;                      // constant or macro, emitted by name

    struct SDL_Window;                              // opaque: usable only through pointers

    struct SDL_Rect { pub int x; pub int y; pub int w; pub int h; }   // layout-checked (§7.4)

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
  to link are given in the package manifest's `native` block
  ([The manifest](03-programs.md#programs.manifest)).

> The standard library may also declare an `extern struct`, a value type implemented by the C++
> class `wyl::Name` (`Weak<T>` is `wyl::Weak`, `String` is `wyl::String`). This is currently both
> the C++ interop value type and the representation of the language's built-in resource values
> (`String`, `Weak<T>`); user code cannot declare one. It has no fields or constructors, its
> default value is the class's default constructor, it doesn't support `==`, and its methods are
> all `extern`: instance methods call the C++ class's member function of the same name.
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
2. A **safe wrapper** in Wylow that owns foreign resources in classes, converts errors to `Result`,
   and exposes only safe types.

Game code uses only the wrapper.

```java
// module platform
import sdl::*;

pub class Window {
    pub static Result<Window, String> create(String title, int w, int h) {
        unsafe {
            var p = SDL_CreateWindow(title.cstr, w, h, 0);
            if (p == null) return Err(String.fromCString(SDL_GetError()));
            return Ok(new Window(p));
        }
    }
    SDL_Window* handle;

    Window(SDL_Window* h) { handle = h; }

    ~Window() {
        unsafe { SDL_DestroyWindow(handle); }
    }
}
```

A class destructor (`~ClassName()`) runs when the last reference is released (see
[Objects](17-memory.md#memory.objects)).

## 7.4 Blittable types

Only **blittable** types (types with an identical memory representation in C) may appear in extern
signatures and extern struct fields. This is checked at compile time.

| Wylow type | C type | Blittable |
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
| Class references, `List<T>`, `T[]`, `Span<T>`, `ref T`, enums with data, nullable values, and `String` (it holds a counted block) | — | ❌ |

### Layout verification

For each extern struct declared with fields, the build checks its size and field offsets against
the C header. If the Wylow declaration does not match it, **the build fails**.

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
  Wylow type is `unsafe`.
- A **panic** inside a callback aborts the process. (Wylow never unwinds, so there is no unwinding
  through C frames.)
- A callback may run on a thread created by the foreign library. Safe code inside it can reach
  only statics, which are read-only and sendable ([Statics](06-variables.md#variables.statics)),
  and the objects it creates; reaching other data requires the `unsafe` userdata cast, and the
  programmer is responsible for avoiding races.

## 7.7 C++ exceptions

Wylow has no exceptions. A C++ exception that would propagate into Wylow code aborts the process. Libraries that use
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
    struct BodyId { pub uint value; }
    BodyId physics_create_box(PhysicsWorld* w, float hx, float hy, float hz);
}
```

## 7.9 Exporting Wylow functions

`@Export` on a top-level function gives it C linkage and a stable, unmangled name so C/C++ code can
call it. Its signature must be blittable.

```java
@Export
int wyl_plugin_version() { return 3; }
```
