# 0047. Standard output is buffered, and an abort may lose what is not yet written

- Status: accepted (2026-11-03)
- Spec: `introduction.aborts`, `introduction.observable`

## Decision

`print` does not flush. Standard output keeps the C library's buffering, and a panic flushes it. An abort can't, so what a program wrote last may be lost when standard output isn't an interactive device.

## Alternatives

- **Flush on every `print`**: nothing is lost, but one million prints to a file took about 2,300 ms against 61 to 73 ms (g++ `-O2`, three runs; MSVC not measured). `print` always ends its line, so flushing on a newline is the same thing.
- **Line buffering everywhere**: Rust's `io::Stdout` does this and [issue 60673](https://github.com/rust-lang/rust/issues/60673) reports it as a performance pitfall. On Win32, [`_IOLBF` behaves as full buffering](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/setvbuf), so it would need its own implementation.
- **A runtime buffer written by a crash handler**: it keeps the speed, but needs a handler per platform that runs on an exhausted stack.

## Why

It is what C specifies: [`stdout` is fully buffered if and only if it can be determined not to refer to an interactive device](https://en.cppreference.com/w/c/io/std_streams.html). [Python's `sys.stdout`](https://docs.python.org/3/library/sys.html#sys.stdout) makes the same split. Checked on a Windows console with MSVC and g++ debug builds: output printed before a stack overflow appears there, and is lost in a file or pipe.
