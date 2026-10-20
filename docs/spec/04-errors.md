# 4. Errors

VPL has **no exceptions**. There are two kinds of failure:

| Kind | Mechanism | Example |
|---|---|---|
| A bug | **Panic** (aborts the process) | Index out of bounds, `x!` on null |
| An expected failure | **`Result<T, E>`** returned as a value | File not found, malformed asset |

Panics are covered in [01-safety.md](01-safety.md#13-panics). This chapter covers `Result`.

## 4.1 `Result<T, E>`

`Result<T, E>` is an enum declared in the **prelude**, VPL source compiled with every program:

```java
enum Result<T, E> {
    Ok(T value),
    Err(E error),
}
```

`Ok(x)` and `Err(e)` can be used directly to construct results wherever the `Result` type is known,
such as in a `return` (see [02-types.md](02-types.md#creating-values)). Elsewhere, write the type:
`Result<Mesh, LoadError>.Ok(mesh)`.

> *Open*: `Result<void, E>` for operations that produce no value. `void` cannot be a type
> argument yet; use another type, such as `bool`, or an enum.

Error types are usually enums carrying data:

```java
enum LoadError {
    NotFound(String path),
    BadFormat(String reason),
}

Result<Mesh, LoadError> loadMesh(String path) {
    if (!File.exists(path)) return Err(LoadError.NotFound(path));
    var bytes = try File.readAll(path);
    ...
    return Ok(mesh);
}
```

## 4.2 Results must be used

Calling a function that returns a `Result` and ignoring the value is a **compile error**:

```java
loadMesh("ship.obj");          // error: unused Result
_ = loadMesh("ship.obj");      // explicitly discarded: OK
```

`_ = expr;` evaluates any value and ignores it; it is an error on a `void` call, which needs no
`_ =`.

## 4.3 `try`

`try expr` evaluates `expr`, which must be a `Result`:

- If it is `Ok(v)`, the expression's value is `v`.
- If it is `Err(e)`, the enclosing function immediately returns `Err(e)`.

`try` must be the **whole value of a statement**: the initializer of a declaration, the value of a
plain `=` assignment, a `return` value, or an expression statement (`try save(file);`). It cannot be
part of a larger expression (`1 + try f()`) or a `for` loop's update; write it as its own
statement. This keeps every early return at the start of a statement, and C++ cannot return from
inside an expression. In an assignment, the object holding the target is still evaluated first.

The enclosing function must return a `Result` with the **same error type**. To change the error
type, convert it explicitly:

```java
var bytes = try File.readAll(path).mapErr(e -> LoadError.NotFound(path));
```

`try` is a prefix keyword (rather than Rust's postfix `?`) because `?` already means "nullable"
in VPL.

## 4.4 Handling results

With an exhaustive `switch`:

```java
switch (loadMesh("ship.obj")) {
    case Ok(Mesh m)                      -> world.add(m);
    case Err(LoadError.NotFound(var p))  -> log("missing " + p);
    case Err(var e)                      -> panic("load failed: " + e);
}
```

With a pattern test:

```java
if (loadMesh(path) is Ok(Mesh m)) { ... }
```

With helper methods:

| Method | Behavior |
|---|---|
| `r.orElse(fallback)` | The value, or `fallback` on error |
| `r.orPanic("message")` | The value, or panic with the message and the error |
| `r.mapErr(f)` | Converts the error type |
| `r.map(f)` | Converts the success value |
| `r.isOk()` / `r.isErr()` | Tests |

> Implemented so far: `orElse`, `orPanic`, `isOk`, and `isErr`. `orPanic` reports the caller's
> location (through `@CallerLocation`), but not yet the error, which needs string formatting.
> `map` and `mapErr` need lambdas.
