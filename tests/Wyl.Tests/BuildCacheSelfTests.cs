using Wyl.Compiler.Build;

// The build cache returns the executable a previous build left when nothing a build depends on has
// changed. A key that ignores one of those inputs is a stale executable, which no test can see: it
// would run the old program and pass.
static class BuildCacheSelfTests
{
    const string Program = """
        #include "wyl.h"
        int main() { return 0; }
        """;

    public static List<TestResult> Run()
    {
        var compiler = NativeCompiler.Find(null, out var error);
        if (compiler is null)
            return [new("build-cache/key", error)];

        var key = compiler.BuildKey(release: false, Program);
        List<string> problems = [];
        if (compiler.BuildKey(release: false, Program) != key)
            problems.Add("the same inputs give different keys");
        if (compiler.BuildKey(release: false, Program + "\n") == key)
            problems.Add("the key ignores the generated C++");
        if (compiler.BuildKey(release: true, Program) == key)
            problems.Add("the key ignores the build flags");
        return [new("build-cache/key", problems.Count == 0 ? null : string.Join('\n', problems))];
    }
}
