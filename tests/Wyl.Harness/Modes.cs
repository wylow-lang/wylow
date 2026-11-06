using Wyl.Compiler;
using Wyl.Compiler.Build;

namespace Wyl.Harness;

/// <summary>The modes tier 2 builds and runs: the interpreter is the reference, the unoptimized build first.</summary>
public static class Modes
{
    /// <param name="release">
    /// Adds a <c>--release</c> build, for a caller whose programs never overflow (the generator's are
    /// guarded): overflow wraps there instead of panicking, so a program that may overflow, like a
    /// fuzzer mutant, legitimately disagrees with the interpreter in it.
    /// </param>
    /// <returns>False when neither MSVC nor g++ is installed; the caller reports that.</returns>
    public static bool TryAvailable(out IReadOnlyList<Mode> modes, bool release = false, bool threadSanitized = false)
    {
        var preferred = NativeCompiler.Find(Toolchain.Msvc, out _) is not null ? Toolchain.Msvc
            : NativeCompiler.Find(Toolchain.Gcc, out _) is not null ? Toolchain.Gcc
            : (Toolchain?)null;
        if (preferred is null)
        {
            modes = [];
            return false;
        }
        var name = preferred == Toolchain.Msvc ? "msvc" : "gcc";
        List<Mode> result =
        [
            new("interpret", null, new CompileOptions(Nest: true, Optimize: false)),
            new(name + "-no-opt", preferred.Value, new CompileOptions(Nest: true, Optimize: false)),
            new(name, preferred.Value, new CompileOptions()),
        ];
        var other = preferred == Toolchain.Msvc ? Toolchain.Gcc : Toolchain.Msvc;
        if (NativeCompiler.Find(other, out _) is not null)
            result.Add(new(other == Toolchain.Msvc ? "msvc" : "gcc", other, new CompileOptions()));
        if (NativeCompiler.Find(Toolchain.ClangSanitized, out _) is not null)
            result.Add(new("asan", Toolchain.ClangSanitized, new CompileOptions()));
        if (threadSanitized && NativeCompiler.Find(Toolchain.ThreadSanitized, out _) is not null)
            result.Add(new("tsan", Toolchain.ThreadSanitized, new CompileOptions()));
        result.Add(new(name + "-no-nest", preferred.Value, new CompileOptions(Nest: false, Optimize: true)));
        if (release)
            result.Add(new(name + "-release", preferred.Value, new CompileOptions(), Release: true));
        modes = result;
        return true;
    }
}
