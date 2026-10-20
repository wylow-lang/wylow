# 4. Errors

VPL has **no exceptions**. There are two kinds of failure:

| Kind | Mechanism | Example |
|---|---|---|
| A bug | **Panic** (aborts the process) | Index out of bounds, `x!` on null |
| An expected failure | **`Result<T, E>`** returned as a value | File not found, malformed asset |

Panics are covered in [01-safety.md](01-safety.md#13-panics). This chapter covers `Result`.

## 4.1 `Result<T, E>`

`Result<T, E>` is a standard library enum:

```java
enum Result<T, E> {
    Ok(T value),
    Err(E error),
}
```

`Ok(x)` and `Err(e)` can be used directly to construct results.

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

## 4.3 `try`

`try expr` evaluates `expr`, which must be a `Result`:

- If it is `Ok(v)`, the expression's value is `v`.
- If it is `Err(e)`, the enclosing function immediately returns `Err(e)`.

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
