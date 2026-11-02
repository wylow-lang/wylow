# 0018. Temporary values can't be written

- Status: accepted (2026-11-01)
- Spec: `structs.methods.mut`

## Decision

A temporary struct or fixed array is a value, not a variable: its `mut` methods can't be called,
its fields and elements can't be assigned, and it can't be passed by `ref` or referred to.

## Alternatives

- **Allowing the write**: it changes a copy no one sees.

## Why

What the write changed would be gone after the statement. Go makes the same writes errors: a
call's result isn't [addressable](https://go.dev/ref/spec#Address_operators), so its fields can't be
[assigned](https://go.dev/ref/spec#Assignment_statements) and its pointer-receiver methods can't be
called on it ([Calls](https://go.dev/ref/spec#Calls)).
