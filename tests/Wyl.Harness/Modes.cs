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
    /// <returns>False when no C++ compiler is installed; the caller reports that.</returns>
    public static bool TryAvailable(out IReadOnlyList<Mode> modes, bool release = false, bool threadSanitized = false)
    {
        var preferred = NativeCompiler.Find(Toolchain.Clang, out _) is not null ? Toolchain.Clang
            : NativeCompiler.Find(Toolchain.Gcc, out _) is not null ? Toolchain.Gcc
            : NativeCompiler.Find(Toolchain.Msvc, out _) is not null ? Toolchain.Msvc
            : (Toolchain?)null;
        if (preferred is null)
        {
            modes = [];
            return false;
        }
        var name = Name(preferred.Value);
        List<Mode> result =
        [
            new("interpret", null, new CompileOptions(Nest: true, Optimize: false)),
            new(name + "-no-opt", preferred.Value, new CompileOptions(Nest: true, Optimize: false)),
            new(name, preferred.Value, new CompileOptions()),
        ];
        var other = preferred == Toolchain.Clang ? Toolchain.Gcc
            : preferred == Toolchain.Gcc ? Toolchain.Clang
            : (Toolchain?)null;
        if (other is { } second && NativeCompiler.Find(second, out _) is not null)
            result.Add(new(Name(second), second, new CompileOptions()));
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

    static string Name(Toolchain toolchain) => toolchain switch
    {
        Toolchain.Clang => "clang",
        Toolchain.Gcc => "gcc",
        _ => "msvc",
    };
}
