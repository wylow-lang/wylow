# 0087. The interpreter runs only the extern functions it implements

- Status: accepted (2026-11-10)
- Spec: none, since the interpreter is part of the implementation and not of the language

## Decision

The interpreter implements the standard library's extern functions, and it finds them by their C++
names. The interpreter refuses to run a program that calls any other extern function, and its
diagnostic names the call. A test that calls such a function is marked `// native` and does not run
in the interpreted mode. The fuzzer does not compare such a test's run with the interpreter.

## Alternatives

- **Load the library and call the function from the interpreter.** The interpreter keeps the value
  that a pointer points to in its own representation, so a pointer that the interpreter passes means
  nothing to C code. A call would also need code generated for each signature, and a `wyl` that is
  compiled ahead of time cannot generate code while it runs. The interpreter could still call a
  function whose arguments are all numbers, through a native library that the C++ compiler builds
  for it. Such functions are a small part of any binding.
- **Simulate selected libraries in the interpreter.** A simulation would be a second implementation
  of the library, and nothing would keep it in step with the real library.

## Why

The interpreter is an oracle for what the compiler does with Wylow code. A call into C++ code is not
Wylow code, so nothing is lost when a program that makes such a call runs only natively. The rule
needs no list of exceptions. The interpreter implements the standard library's extern functions
because they belong to the language, and every other extern function is native.
