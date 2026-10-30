namespace Vpl.Dashboard;

public sealed record ReferenceItem(string Title, string Description, string Command, string? Href = null, string? Note = null);
public sealed record ReferenceGroup(string Title, IReadOnlyList<ReferenceItem> Items);

public static class Reference
{
    public static IReadOnlyList<ReferenceGroup> All { get; } =
    [
        new("Build", [
            new("Build everything", "Compile the compiler, the CLI, and both test harnesses.", "dotnet build"),
        ]),
        new("Test suite", [
            new("All six modes, in parallel", "Pre-commit: the interpreter, MSVC, g++, --no-opt, --no-nest, and ASan, each in its own process.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll --all-modes", Href("#/tests", ("cc", "all"))),
            new("All tests, MSVC", "The default mode. Every case, the matrices, and the self-tests.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll", Href("#/tests", ("cc", "msvc"))),
            new("All tests, g++", "The same suite through g++.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll --cc gcc", Href("#/tests", ("cc", "gcc"))),
            new("All tests, ASan", "clang-cl with AddressSanitizer and UBSan; any report fails.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll --cc asan", Href("#/tests", ("cc", "asan"))),
            new("No optimizations", "Runs with --no-opt; the unoptimized IR is the program's meaning.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll --no-opt", Href("#/tests", ("noOpt", "true"))),
            new("No nesting", "Declares every temporary instead of nesting it into its use.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll --no-nest", Href("#/tests", ("noNest", "true"))),
            new("Filter by path", "Runs only tests whose path contains the text.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll structs", Href("#/tests", ("filter", "structs"))),
            new("Matrices only", "Runs only the interaction matrices.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll matrix", Href("#/tests", ("filter", "matrix"))),
            new("Update matrix snapshots", "Records what every matrix cell does now; review the diff in the same change.", "dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll matrix --update-snapshots", Note: "Not wired to the dashboard yet; copy the command."),
        ]),
        new("Fuzz", [
            new("Tier 1, two minutes", "Compiles mutants in-process; thousands a minute. Part of the pre-commit routine.", "dotnet tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll --seconds 120", Href("#/fuzz")),
            new("Tier 2, fifteen minutes", "Builds and runs every compiling mutant in six modes. Belongs in the background.", "dotnet tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll --tier 2 --seconds 900", Href("#/fuzz", ("tier", "2"), ("seconds", "900"))),
            new("Replay a finding", "Rebuilds one mutant of a seed, using the index in the finding file.", "dotnet tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll --seed 7 --tier 2 --replay 42", Href("#/fuzz")),
            new("Self-test the fuzzer", "Checks that mutations still apply and generation is deterministic.", "dotnet tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll --self-test"),
            new("List mutations", "Prints the mutation catalog.", "dotnet tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll --list"),
            new("Fuzz options", "Seed (deterministic), time budget, mutant cap, workers, corpus filter, error tests, warning strictness, and keeping the run directory.", "dotnet tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll --seed 1 --seconds 120 --tier 1 --max 0 --jobs 8 --filter values --errors --no-strict --keep", Href("#/fuzz")),
        ]),
        new("Mutation testing", [
            new("One target", "Mutates one compiler file, rebuilds it into a private copy, and reruns its covering tests. Survivors are written to tests/mutation/results.", "dotnet tests/Vpl.Mutation/bin/Debug/net10.0/Vpl.Mutation.dll --target ir-effects", Href("#/mutation")),
            new("Sampled run", "Runs the first N mutants of a file.", "dotnet tests/Vpl.Mutation/bin/Debug/net10.0/Vpl.Mutation.dll --target conversions --max 20", Href("#/mutation")),
            new("Time budget", "Stops after the budget and records the score of what ran.", "dotnet tests/Vpl.Mutation/bin/Debug/net10.0/Vpl.Mutation.dll --target emitter-ir --seconds 1800", Href("#/mutation")),
            new("List the mutants", "Prints every mutation site of a target without running anything.", "dotnet tests/Vpl.Mutation/bin/Debug/net10.0/Vpl.Mutation.dll --target place-path --list"),
            new("List the targets", "Prints the six files and the test filters that cover each.", "dotnet tests/Vpl.Mutation/bin/Debug/net10.0/Vpl.Mutation.dll --list-targets"),
        ]),
        new("Compiler CLI", [
            new("Run a program", "Builds and runs a .vpl file.", "dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll run examples/hello.vpl"),
            new("Build an executable", "Writes the executable next to the source unless -o is given.", "dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll build examples/hello.vpl -o hello.exe"),
            new("Check only", "Reports errors without building C++.", "dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll check examples/hello.vpl"),
            new("Emit C++", "Prints the generated C++ to standard output.", "dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll emit examples/hello.vpl"),
            new("Print IR", "Prints the mid-level IR of the program's functions.", "dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll ir examples/hello.vpl"),
            new("CLI options", "Release, compiler choice, output path, nesting, and optimizations.", "vpl <run|build|check|emit|ir> <file.vpl> [--release] [--cc msvc|gcc|asan] [-o out.exe] [--no-nest] [--no-opt]"),
        ]),
        new("Coverage", [
            new("All six modes", "Runs the suite under dotnet-coverage in every mode and merges the results.", "bash tools/coverage.sh", Note: "Not wired to the dashboard yet; copy the command."),
            new("One mode", "A quicker look; a line it misses may be covered by a mode that didn't run.", "VPL_COVERAGE_MODES=msvc bash tools/coverage.sh", Note: "Not wired to the dashboard yet; copy the command."),
        ]),
        new("Generated C++", [
            new("Warnings are errors", "Check generated C++ by hand; a warning here is an emitter bug.", "g++ -std=c++20 -Wall -Wextra -Wconversion -Wsign-conversion -Wshadow -fsyntax-only -Iruntime tests/.out/<test>/<file>.cpp"),
        ]),
    ];

    static string Href(string path, params (string Key, string Value)[] parameters) =>
        path + (parameters.Length == 0
            ? ""
            : "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
}
