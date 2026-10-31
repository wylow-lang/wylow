using Wyl.Compiler;
using Wyl.Compiler.Build;

namespace Wyl.Harness;

/// <summary>The modes tier 2 builds and runs: the interpreter is the reference, the unoptimized build first.</summary>
public static class Modes
{
    /// <returns>False when neither MSVC nor g++ is installed; the caller reports that.</returns>
    public static bool TryAvailable(out IReadOnlyList<Mode> modes)
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
        result.Add(new(name + "-no-nest", preferred.Value, new CompileOptions(Nest: false, Optimize: true)));
        modes = result;
        return true;
    }
}
