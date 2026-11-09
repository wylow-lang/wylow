# 0066. One module, `std::threading`, for the types threads use

- Status: accepted (2026-11-09); moves the modules that 0056, 0059, and 0060 named
- Spec: `library.threading`, `library.thread`, `library.channel`, `library.isolated`

## Decision

- The module `std::threading` declares `Thread<T>`, `spawn`, and `join`, the channel types `Channel<T>`,
  `Sender<T>`, and `Receiver<T>`, and the box `Isolated<T>`. The locks and atomics that are still to
  come go in it too. The modules `std::thread`, `std::channel`, and `std::isolated` no longer exist.
- Each type keeps a file of its own in the module's directory: `thread.wyl`, `channel.wyl`, and
  `isolated.wyl`.
- The binder names an extern function of a module `wyl::<module>::<name>`, so the runtime declares
  every extern function of these types in `wyl::threading`. The channel's runtime state is now
  `wyl::threading::ChannelState<T>`, since the namespace holds more than one state type.
- The fuzzer's corpus leaves out the tests marked `// tsan` instead of the files that import a
  threading module. Every test that starts a thread carries the marker, and a test of `Isolated<T>` or
  of a channel on one thread has output stable enough to compare.

## Alternatives

- **A module for each type**, as the standard library had. A channel, a box, and a mutex are each one
  type, so each module held one type. A program that starts a thread and sends it values imported
  from three modules.
- **The existing name `std::thread`.** Imports of `spawn` would not change, but a module named after
  one of its types reads as that type's module, and `Channel` and `Mutex` would be hidden in it.
- **`std::concurrency`**, after the spec's chapter. It is the longest name to write in every import.
- **Threads apart from the synchronization types.** Rust declares threads in
  [`std::thread`](https://doc.rust-lang.org/std/thread/index.html) and its channels, mutexes, and
  atomics in [`std::sync`](https://doc.rust-lang.org/std/sync/index.html). In Wylow every one of these
  types exists to move or share values between threads, so the split separates types that a program
  uses together.

## Why

A module groups the declarations a program uses together, and a program that uses a channel or a
mutex also starts a thread. C# declares its threads and locks in
[`System.Threading`](https://learn.microsoft.com/en-us/dotnet/api/system.threading), and Python
declares `Thread` and `Lock` in [`threading`](https://docs.python.org/3/library/threading.html). The
name `std::threading` names the subject that the types share rather than one of the types.
