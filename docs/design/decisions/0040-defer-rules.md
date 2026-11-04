# 0040. A deferred statement cannot jump out

- Status: accepted (2026-11-03)
- Spec: `statements.defer`

## Decision

`defer` stays. A `return`, `try`, `break`, or `continue` that would leave a deferred statement is a compile-time error. A deferred statement is block-scoped, registered when reached, evaluated when it runs, and runs in reverse order with the releases of the block's locals, after a `return`'s value is computed.

## Alternatives

- **A deferred statement may jump out**, overriding the exit in progress. A computed return value would be discarded, and a block's exit would depend on what its deferred statements do.
- **Function-scoped, with arguments evaluated at the `defer`**: Go's [defer statements](https://go.dev/ref/spec#Defer_statements). A `defer` in a loop would then wait for the function to end.

## Why

Swift's [defer statement](https://github.com/swiftlang/swift-book/blob/main/TSPL.docc/ReferenceManual/Statements.md) "can't transfer program control outside of the defer statement", and Zig's [defer](https://ziglang.org/documentation/master/#defer) rejects `return` the same way; both run at scope exit in reverse order. It stays because a library scope guard can't use the surrounding locals: second-class values can't be stored in a struct field ([0019](0019-function-values.md)). `defer` isn't implemented, so the error has no catalog code yet.
