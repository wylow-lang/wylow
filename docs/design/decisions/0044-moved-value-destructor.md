# 0044. A moved value's destructor runs on the thread that releases it

- Status: accepted (2026-11-03)
- Spec: `concurrency.sendable.moving`

## Decision

A unique value moved to another thread runs its destructor on the thread that releases it. This confirms what [0028](0028-destructors-on-unique-structs.md) assumed.

## Alternatives

- **On the thread that created it**: it would need a way to send the value back to a thread that may have ended.

## Why

It is the rule of every other move: the owner at the end releases the value. A type that must stay on one thread is not sendable.
