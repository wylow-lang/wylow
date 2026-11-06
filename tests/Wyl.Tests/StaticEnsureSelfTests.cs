using Wyl.Compiler;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Text;
using Wyl.Harness;

static class StaticEnsureSelfTests
{
    const string Fixture = """
        struct Config {
            int n = Settings.LIMIT;
        }

        class Settings {
            static int LIMIT = 3;
            static int OTHER = LIMIT + readLimit();
            static int LATER = OTHER;
            static Config CONFIG;
        }

        int readLimit() { return Settings.LIMIT; }
        int onlyMain() { return Settings.LIMIT; }

        void main() {
            print(Settings.LIMIT);
            print(Settings.CONFIG.n);
            print(onlyMain());
            escaping fn() -> int read = () -> Settings.OTHER;
            print(read());
        }
        """;

    public static List<TestResult> Run()
    {
        var source = new SourceText("static_ensures.wyl", TestPrograms.WithStd(Fixture));
        var meaning = Compilation.Lower(source, new CompileOptions(Optimize: false));
        var optimized = Compilation.Lower(source);
        if (meaning is not { Ir: { } plain, Program: { } plainProgram } || optimized is not { Ir: { } kept, Program: { } keptProgram })
            return [new("static-ensures/fixture", string.Join('\n', meaning.Diagnostics.Concat(optimized.Diagnostics)))];
        List<(string Name, Func<string?> Check)> cases =
        [
            ("static-ensures/meaning-main", () => Count(plain, Function(plain, "main"), 2)),
            ("static-ensures/meaning-main-only-callee", () => Count(plain, Function(plain, "onlyMain"), 1)),
            ("static-ensures/meaning-initializer", () => Count(plain, Init(plain, "OTHER"), 1)),
            ("static-ensures/meaning-default", () => Count(plain, Default(plain, plainProgram, "Config"), 1)),
            ("static-ensures/meaning-lambda", () => Count(plain, Lambda(plain), 1)),
            ("static-ensures/optimized-main", () => Count(kept, Function(kept, "main"), 0)),
            ("static-ensures/optimized-main-only-callee", () => Count(kept, Function(kept, "onlyMain"), 0)),
            ("static-ensures/optimized-initializer", () => Count(kept, Init(kept, "OTHER"), 1)),
            ("static-ensures/optimized-later-initializer", () => Count(kept, Init(kept, "LATER"), 1)),
            ("static-ensures/optimized-default", () => Count(kept, Default(kept, keptProgram, "Config"), 1)),
            ("static-ensures/optimized-initializer-callee", () => Count(kept, Function(kept, "readLimit"), 1)),
            ("static-ensures/optimized-lambda", () => Count(kept, Lambda(kept), 0)),
        ];
        return [.. cases.Select(c => Run(c.Name, c.Check)), SameOutput(source)];
    }

    static int Count(IrProgram ir, IrFunction function) =>
        IrWalk.Descendants(function.Body, intoLambdas: false)
            .OfType<IrDo>()
            .Count(statement => statement.Value is IrCall call && ir.StaticOf(call.Method.Symbol) is not null);

    static string? Count(IrProgram ir, IrFunction function, int expected) =>
        Count(ir, function) == expected ? null : $"{function.Name} has {Count(ir, function)} ensure calls, expected {expected}";

    static IrFunction Function(IrProgram ir, string name) =>
        ir.Functions.First(function => function.Symbol.Name == name && !function.IsLambda && function.Symbol.ContainingType is null);

    static IrFunction Init(IrProgram ir, string field) =>
        ir.Statics.Single(statik => statik.Field.Name == field).Initializer;

    static IrFunction Default(IrProgram ir, BoundProgram program, string type) =>
        ir.Functions.Default(program.Types.First(declared => declared.Name == type))!;

    static IrFunction Lambda(IrProgram ir) =>
        IrWalk.Descendants(Function(ir, "main").Body, intoLambdas: false)
            .OfType<IrLet>()
            .Select(let => let.Value switch
            {
                IrLambda lambda => lambda.Function,
                IrClosure closure => closure.Function,
                _ => null,
            })
            .OfType<IrFunction>()
            .Single();

    static TestResult SameOutput(SourceText source)
    {
        var meaning = Compilation.Interpret(source, new CompileOptions(Optimize: false));
        var optimized = Compilation.Interpret(source);
        if (meaning.Interpretation is not { Status: InterpretationStatus.Completed } expected)
            return new("static-ensures/same-output", $"the meaning ended as {meaning.Interpretation?.Status}: {meaning.Interpretation?.Error.Trim()}");
        if (optimized.Interpretation is not { Status: InterpretationStatus.Completed } actual)
            return new("static-ensures/same-output", $"the optimized IR ended as {optimized.Interpretation?.Status}: {optimized.Interpretation?.Error.Trim()}");
        return new("static-ensures/same-output", expected.OutputText == actual.OutputText
            ? null
            : $"the optimized IR printed '{actual.OutputText}', the meaning printed '{expected.OutputText}'");
    }

    static TestResult Run(string name, Func<string?> check)
    {
        try
        {
            return new(name, check());
        }
        catch (Exception e)
        {
            return new(name, $"exception: {e}");
        }
    }
}
