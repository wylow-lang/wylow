# VPL Dashboard

A local browser UI for the compiler's testing tools: build, run the suite, fuzz with a seed, replay a
mutant, and triage findings without remembering flags.

```
dotnet run --project tools/Dashboard
```

Then open <http://localhost:5178>. Options: `--port 5178`, `--open`, `--root <repo>`.

| Page | |
|---|---|
| Overview | repository, toolchains, build freshness, code size, recent runs, recent seeds |
| Code | C# by project, VPL standard library, tests and examples, C++ runtime, and other sources, by physical and code line counts |
| Tests | compiler, filter, `--no-opt`/`--no-nest`, build first, live output, failures linked to source/C++/IR |
| Fuzz | seed, tier, time budget, workers, filter; live stats; replay a mutant; recent seeds |
| Findings | `tests/fuzz/findings/*.vpl` and run logs, with the reduced source and replay |
| Reference | every documented command and flag, with copy buttons and links to the matching form |

Every job is a child process of the existing runners, and the exact argv is shown before starting and
as the first line of the log. The server builds argv arrays; it never takes a command string from the
browser. It binds to localhost only, and cross-origin POSTs are refused.

History and logs live in `tools/Dashboard/.state/` (gitignored). Cancel kills the process tree. A
build or a suite with "Build first" waits for running jobs, since it overwrites assemblies a running
test or fuzzer has loaded. Fuzz runs may run alongside each other (each uses its own run directory
under `tests/.out/fuzz/`); suites serialize because they share `tests/.out/`.

The project is not in `vpl.slnx`. It references `Vpl.Compiler` only to reuse `NativeCompiler.Find`,
so `dotnet run --project tools/Dashboard` builds the compiler too. If the compiler does not build,
the dashboard cannot start either; run `dotnet build` to see the error.

To add a tool, add a `JobSpec` in `JobSpecs.cs`: its parameters, argv builder, and optional output
observer are one definition the API and the UI form share.
