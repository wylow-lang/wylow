# 0021. A switch matches its value in place

- Status: accepted (2026-11-01)
- Spec: `patterns.cases`

## Decision

A switch matches its value where it is, without a copy of its own; each case tests the value as it
is when that case is tried. A program that needs the value as it was copies it first.

## Alternatives

- **A hidden copy of the switched value**, so guards can't change what later cases test.

## Why

A hidden copy would cost a large struct's copy on every switch, unless the optimizer removed it, so the copy is written where it is wanted.
