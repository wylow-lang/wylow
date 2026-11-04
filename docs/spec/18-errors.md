<a id="errors"></a>
# 18. Errors and panics

An operation that fails in a way the program should handle returns a `Result` value; a failed
check, or a call of `panic`, ends the program. This chapter gives panics, `Result`, and how a
program handles a result. The flow of `try` is a rule of expressions
([`try`](13-expressions.md#expressions.try)).

<a id="errors.panics"></a>
## 18.1 Panics

A program panics when one of the checks this specification describes fails, or when it calls
`panic(message)` ([`std::panic`](a-library.md#library.panic)). The program then ends, as
[Panics](01-introduction.md#introduction.panics) describes: no destructor or deferred statement
runs, and no other thread continues. A panic can't be caught.

The location of a panic is the location of the operation that failed, or, inside a function marked
`@CallerLocation`, the location of the call to that function
([`@CallerLocation`](22-annotations.md#annotations.caller-location)). It is written as the file's
path under the package's `src/` directory, a line, and a column
([Source text](02-lexical.md#lexical.source)): `geometry/shapes.wyl:3:5`.

<a id="errors.result"></a>
## 18.2 `Result`

`Result<T, E>` is an enum declared in the standard library, which every file sees without an import
([Names every file sees](03-programs.md#programs.builtin-names)). It holds either a value of type
`T`, the success, or a value of type `E`, the error:

```java
enum Result<T, E> {
    Ok(T value),
    Err(E error),
}
```

`Ok(x)` and `Err(e)` are written directly where the type of the result is known, such as the value
of a `return` or an argument of a function that has one `Result` type
([Implicit variants](09-enums.md#enums.variants.implicit)). Elsewhere the type is written:
`Result<Order, ReadError>.Ok(order)`.

An error type is usually an enum that carries what went wrong:

```java
enum ReadError {
    Missing(String path),
    BadFormat(String reason),
}

Result<String, ReadError> readText(String path) { ... }

Result<Order, ReadError> readOrder(String path) {
    var text = try readText(path);
    ...
    return Ok(order);
}
```

`try` returns the error through the enclosing function
([`try`](13-expressions.md#expressions.try)). The standard library's declaration of `Result` also
gives methods that use a result or one of its values; its doc comments give them.

<a id="errors.unused"></a>
## 18.3 Unused results

A call that returns a `Result` reports a failure the program should handle or choose to ignore.

- It is a compile-time error to call a function that returns a `Result` as a statement. The result
  is used, returned, or discarded with `_ = expr;`
  ([Discarding a value](14-statements.md#statements.expressions.discard)).

**Example:**

```java
enum ReadError { Missing }

Result<int, ReadError> read() {
    return Ok(1);
}

Result<int, ReadError> readTwice() {
    return Ok(try read() * 2);
}

void main() {
    read();                         // error[WYL0421]
    _ = read();
    int value = read();             // error[WYL0079]
}
```

<a id="errors.handling"></a>
## 18.4 Handling results

A `switch` over a `Result` handles its two variants, and is exhaustive when both are covered
([Exhaustiveness](15-patterns.md#patterns.exhaustiveness)):

```java
switch (readOrder(path)) {
    case Ok(var order)                    -> process(order);
    case Err(ReadError.Missing(var path)) -> log($"missing {path}");
    case Err(ReadError.BadFormat(var why)) -> log($"bad format: {why}");
}
```

`is` tests which variant a result is, and binds its value
([Kinds of patterns](15-patterns.md#patterns.kinds)):

```java
if (readOrder(path) is Ok(var order)) {
    process(order);
}
```

There is no `isOk()` and no `isErr()`: `r is Ok` and `r is Err` are the tests.
