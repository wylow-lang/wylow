using Wyl.Compiler.Build;
using Wyl.Compiler.CodeGen;
using Wyl.Compiler.Text;

// A failed link names its cause from the linker's own output. These tests give the classification the
// output of each linker, so that a format no local toolchain prints, such as GNU ld's on Linux, is
// still read. The outputs are the linkers' own, with shorter paths.
static class BuildFailureSelfTests
{
    const string Cpp = @"C:\out\main.cpp";

    public static List<TestResult> Run()
    {
        var source = new SourceText(@"C:\pkg\src\main.wyl", "extern { int parse_count(int limit); }\n");
        var declaration = new Location(source, new TextSpan(13, 11));
        var interop = new CppInterop([], [new InteropRegion(1, 9, declaration, "extern function 'parse_count'", "parser::parse_count")]);
        // The classification asks for the sources of native/, so it needs them on disk.
        var native = NativeWithSource();

        List<TestResult> results = [];
        void Declared(string linker, string output)
        {
            var build = BuildFailure.Classify(output, Cpp, interop, native);
            var codes = string.Join(", ", build.Diagnostics.Select(d => $"{d.Code} at {d.Location.Span.Start}"));
            results.Add(new($"build-failure/declared-{linker}",
                codes == "WYL0519 at 13" ? null : $"expected WYL0519 at the declaration, got '{codes}' and: {build.Error}"));
        }
        void Used(string linker, string output)
        {
            var build = BuildFailure.Classify(output, Cpp, interop, native);
            const string expected = "'PathIsRelativeA', which native source 'detail/shim.cpp' uses";
            results.Add(new($"build-failure/used-{linker}",
                build.Diagnostics.Count == 0 && build.Error is { } error && error.Contains(expected, StringComparison.Ordinal)
                    ? null
                    : $"expected an error that names {expected}, got: {build.Error ?? string.Join('\n', build.Diagnostics)}"));
        }
        void Generated(string linker, string output)
        {
            var build = BuildFailure.Classify(output, Cpp, interop, native);
            results.Add(new($"build-failure/generated-{linker}",
                build.Error?.StartsWith("internal compiler error", StringComparison.Ordinal) == true
                    ? null
                    : $"a symbol of the generated program that no declaration names is a compiler bug, got: {build.Error ?? string.Join('\n', build.Diagnostics)}"));
        }

        Declared("msvc", """
            main.obj : error LNK2019: unresolved external symbol "int __cdecl parser::parse_count(int)" (?parse_count@parser@@YAHH@Z) referenced in function "int __cdecl wyl_extern_1<void>(int)" (??$wyl_extern_1@X@@YAHH@Z)
            C:\out\main.exe : fatal error LNK1120: 1 unresolved externals
            """);
        Declared("lld", """
            lld-link: error: undefined symbol: int __cdecl parser::parse_count(int)
            >>> referenced by C:\out\main.obj:(int __cdecl wyl_extern_1<void>(int))
            """);
        Declared("ld", """
            /usr/bin/ld: /tmp/ccQ1x2.o: in function `wyl_extern_1<void>(int)':
            main.cpp:(.text+0x15): undefined reference to `parser::parse_count(int)'
            collect2: error: ld returned 1 exit status
            """);
        Used("msvc", """
            0_shim.obj : error LNK2019: unresolved external symbol __imp_PathIsRelativeA referenced in function "int __cdecl relative(void)" (?relative@@YAHXZ)
            C:\out\main.exe : fatal error LNK1120: 1 unresolved externals
            """);
        Used("lld", """
            lld-link: error: undefined symbol: __declspec(dllimport) PathIsRelativeA
            >>> referenced by C:\out\native-objects\0_shim.obj:(int __cdecl relative(void))
            """);
        Used("ld", """
            C:/msys64/ucrt64/bin/ld.exe: C:\Temp\ccA1b2.o:shim.cpp:(.text+0x1f): undefined reference to `__imp_PathIsRelativeA'
            collect2.exe: error: ld returned 1 exit status
            """);
        Generated("msvc", """
            main.obj : error LNK2019: unresolved external symbol "void __cdecl wyl::missing(void)" (?missing@wyl@@YAXXZ) referenced in function main
            """);
        Generated("ld", """
            /usr/bin/ld: main.cpp:(.text+0x9): undefined reference to `wyl::missing()'
            """);
        return results;
    }

    /// <summary>A <c>native/</c> directory with one source at <c>detail/shim.cpp</c>, which the classification maps object files to.</summary>
    static NativeInputs NativeWithSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wyl-build-failure", "native");
        Directory.CreateDirectory(Path.Combine(directory, "detail"));
        File.WriteAllText(Path.Combine(directory, "detail", "shim.cpp"), "int relative();\n");
        return new NativeInputs([new PackageNative(Package.Named("example", PackageKind.Program, new Dictionary<string, Package>()), directory, [], [], [], [], [])]);
    }
}
