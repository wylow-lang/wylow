# Next: finish the concurrency review's fixes, before `Mutex`

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Do this work before `concurrency-step-7.md`. A review of the
threads, channels, `Isolated<T>`, and constructor work found the problems below. `Mutex` builds on the
same runtime and the same interpreter code, so these fixes come first. Read `docs/design/runtime.md`
and `docs/design/testing.md` first.

The review's other findings are fixed: the race in `Weak.get` on a shared object, virtual `Retain` and
`Release`, the constructors that left a field's default zeroed, the pool leak, and the pauses that grew
with the heap. Those commits are `1d31b09`, `bf4dfb0`, `fcd5aea`, `b4e3ed9`, and `ff49dbb`.

## How to work

- Ask the user whether CI passed on those commits, and fix any failure first. These runs have not
  happened on a developer machine: the TSan mode, which cannot run on Windows, and the native matrix.
- After you write a test for a bug, put the bug back and confirm that the test fails. During the
  review, this check found a test that could not fail and a test that missed overlapping objects.
- A race needs a stress program that runs long enough to hit it. A hang is a finding, so diagnose it.
  One hang during the review was a real bug in a new fix, and only a check that every object is
  destroyed exactly once showed it.
- Keep every run short, with a timeout. A filtered native run takes about 45 seconds because the runner
  also runs its self-tests, so run several filters in parallel.

## Bugs

### 1. Dropped channel values are destroyed in a different order by each executor

This bug is reproduced. `detachSender` and `detachReceiver` in `runtime/wyl/channels.h` let a
`std::deque` destroy the dropped values, and the order then depends on the C++ standard library.

```
unique struct Item { pub int id; ~Item() { print($"item {id}"); } }

void main() {
    var channel = new Channel<Item>();
    { var sender = channel.sender(); sender.send(Item(1)); sender.send(Item(2)); sender.send(Item(3)); }
    print("closed");
}
```

The native build prints `item 3`, `item 2`, `item 1`, and the interpreter prints them from 1 to 3. The
test `channels/dropped_on_close` queues one value, so it cannot see the difference. Pop the values from
the front in a loop, outside the lock, and make the test queue at least two values. State the order in
the spec if the spec does not state it already.

### 2. A receiver in the interpreter can spin

This bug comes from reading the code and is not reproduced. In `receiveValue` in
`Interpreter.Channels.cs`, a sender may call `Ready.Set()` after the receiver has taken the value and
called `Ready.Reset()`. The event then stays set while the queue is empty, and the next receive spins
until another value arrives. The branch that finds an empty queue never resets the event. Reset it
there, under the lock.

### 3. The executors may release a thread's closure at different times

This bug comes from reading the code and is not reproduced. `RunThreadBody` in
`Interpreter.Threads.cs` collects cycles and then releases the closure. The C++ thread destroys its
closure first, and it collects cycles when the thread's state ends. A destructor that prints would show
the difference. A caller usually still holds the closure, which hides the order, so a test needs a
closure whose last reference is the thread's. Make the interpreter follow the C++ order.

### 4. A thread that cannot start does not panic

This bug comes from reading the code and is not reproduced. If `std::thread` fails in `spawnThread` in
`runtime/wyl/threads.h`, the exception ends the program without a Wylow panic, and the handle leaks.
Catch `std::system_error`, delete the handle, and raise a panic with the caller's location.

## Two answers to one question

### 5. Member bounds

`ApplyMemberBounds` in `Binder.Declarations.cs` sets `IsCopyableBound` and `IsSendableBound` on a type's
shared parameter symbols and undoes the change after the body. `IsCopyableIn` and `IsSendableIn` answer
the same question through `function.CopyableParameters` and `function.SendableParameters`. Keep one
mechanism and delete the other. The second one changes no shared state, so prefer it if every caller can
name the function. Check `Binder.Arguments.cs` and `Binder.Statements.cs`, which call the first one.

### 6. The isolated check

`Symbol.IsIsolated` and `StdlibSymbols.IsIsolated` answer one question. The architecture table documents
the split, and it omits the reader in `IrEffects.cs`. Keep one, move every caller, and correct the table.

### 7. The interpreter's second dispatch

`Interpreter.Intrinsics.cs` lists each thread, channel, and isolated intrinsic in its table, and then
`ThreadIntrinsic`, `ChannelIntrinsic`, and `IsolatedIntrinsic` switch on the same names as strings. The
`interpreter/registry` self-test sees only the table, so a wrong name in a `switch` fails only when a
program runs it. Give each intrinsic its own method in the table, so that a wrong name cannot compile.

## Duplication and small cleanups

- `CheckStaticField` and `CheckSharedField` in `Binder.Declarations.cs` repeat the sendable and copyable
  checks. The same file repeats the `PropertyBound` switch, and item 5 may remove one copy.
- `detachSender` and `detachReceiver` in `Interpreter.Channels.cs` repeat the release of dropped values.
- `ProcessNested` in `InUse.cs` repeats the case analysis in `InnerBlocks`.
- `receiveValue` has two conditionals that do nothing. `NullOf(nullable == element ? element : nullable)`
  is always `NullOf(nullable)`, and two arms of the ternary before it are the same.
- In `InUse.cs`, the `Referent` overload for `IrRvalue` never uses `places`, and `ReferenceIsStored` is
  only `is IrStore`.
- `FunctionType.Forms.Get` in `TypeKinds.cs` allocates a `FunctionType` on every call, even when the slot
  already holds one. Read the slot first.
- `ProbeThreadSanitized` in `NativeCompiler.cs` never deletes its `wyl-tsan-probe-<pid>` directory.
- `is A or B && env is not null` in `NativeCompiler.cs` is correct but easy to misread. Add parentheses.

## Split the large files

Architecture.md says that a file past about 600 lines is split. Split these files by topic, in commits
that move code and change nothing else, so that a real change never hides in the diff:

| File | Lines |
|---|---|
| `IrVerifier.Values.cs` | 1183 |
| `Binder.Declarations.cs` | 924 |
| `CppEmitter.Ir.cs` | 767 |
| `IrVerifier.cs` | 717 |
| `Binder.Expressions.cs` | 709 |
| `Interpreter.cs` | 652 |
| `Binder.Patterns.cs` | 630 |

## Finish the review

The review read the runtime, the interpreter's concurrency code, constructors, and moves closely. It only
skimmed the verifier, the emitter, the parser, and pattern lowering, or tested them from outside. It did
not read spec chapters 18 to 22, which came from the same run of commits. Read these areas, and report
what you find before you change anything large.

## Decisions for the user

Ask about each of these. None of them blocks `Mutex` unless the user says so.

- **The bins.** The 8 bins are the largest part of `runtime/wyl/pools.h`: `Floor`, `Link`, `Unlink`, and
  most of `Rebin` exist so that new objects fill the fullest chunk. The user wants a very simple
  allocator. One list of partial chunks per size class would remove that code, and it would probably
  cost more fragmentation. Measure fragmentation on a mixed workload both ways before anyone decides.
- **A constant-time remote drain.** `DrainRemote` walks the list to count its slots and find its tail,
  which took up to about 24 microseconds in the review's benchmark. A chunk is 64 KiB and aligned, so
  one 32-bit word could hold the list's head as an offset and its count. Then a drain would splice the
  list without walking it. Do this only if the remaining pauses matter.
- **The global heap.** `List` buffers, built strings, objects over 512 bytes, and shared objects use the
  system allocator, so its pauses apply to them. Small strings and shared objects could use the pools,
  now that a free from another thread works.
- **`CycleCollector::Add`.** A plain `Release` of a cyclic object can grow the buffer or run `Compact`,
  and each costs time in proportion to the buffer's size. This is the same kind of pause that the pools
  no longer have.
