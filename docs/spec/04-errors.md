# 4. Errors

Wylow has **no exceptions**. There are two kinds of failure:

| Kind | Mechanism | Example |
|---|---|---|
| A bug | **Panic** (aborts the process) | Index out of bounds, `x!` on null |
| An expected failure | **`Result<T, E>`** returned as a value | File not found, malformed asset |

Panics are covered in [Panics](18-errors.md#errors.panics). This chapter covers `Result`.

## 4.1 `Result<T, E>`

`Result<T, E>` is an enum declared in the standard library, which every file sees without
importing it ([Names every file sees](03-programs.md#programs.builtin-names)):

```java
enum Result<T, E> {
    Ok(T value),
    Err(E error),
}
```

`Ok(x)` and `Err(e)` can be used directly to construct results wherever the `Result` type is known,
such as in a `return` (see [02-types.md](09-enums.md#enums.variants.implicit)). Elsewhere, write the type:
`Result<Mesh, LoadError>.Ok(mesh)`.

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

`try` can be used anywhere in an expression (`1 + try parse(s)`, `print(try load(path))`), a `for`
loop's update included. It returns when it is evaluated, so what the expression evaluated before
it has happened, and the rest isn't evaluated; an operand that isn't evaluated, such as the right
side of `&&` when the left is false, doesn't return. In an assignment, the object holding the
target is evaluated first, as always. The statement's temporaries are released by the return, the
last made first, before the function's locals.

The enclosing function must return a `Result` with the **same error type**. To change the error
type, convert it explicitly:

```java
var bytes = try File.readAll(path).mapErr(e -> LoadError.NotFound(path));
```

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

There are no `isOk()` and `isErr()`: `r is Ok` and `r is Err` test which variant a result is.
