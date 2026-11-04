# 0043. No constant declaration and no top-level variables

- Status: accepted (2026-11-03)
- Spec: `variables.constants`, `variables.statics`, `interop.extern`

## Decision

A value fixed for the run of a program is a static. The language has no constant declaration and no top-level variable. `const` stays reserved with no meaning, and an extern block declares a C++ constant or macro as `static int MAX_TOKENS;`.

## Alternatives

- **A `const` declaration**: a second way to name a fixed value beside read-only statics.
- **`const` only in extern blocks**: closer to the C++ being declared, but a keyword with a meaning in one place.

## Why

Statics are already read-only ([0015](0015-threading-model.md)), so a constant would add nothing a static lacks.
