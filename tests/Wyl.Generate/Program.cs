using System.Globalization;
using Wyl.Generate;
using Wyl.Harness;

// Makes well-typed Wylow programs from a seed. Stage 1 uses integers, bool, char, float, double, and
// String; every expression is exactly the type asked for, every integer operation is guarded against
// overflow, and every loop has a literal bound, so a program cannot panic or make no progress.
// Usage: Wyl.Generate [--seed N] [--max N] [--stage 1] [--size N] [--self-test]

int seed = 1;
int max = 1;
int stage = 1;
int size = 40;
bool selfTest = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--max" when i + 1 < args.Length: max = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--stage" when i + 1 < args.Length: stage = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--size" when i + 1 < args.Length: size = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--self-test": selfTest = true; break;
        default:
            Console.Error.WriteLine($"unknown option '{args[i]}'");
            return 1;
    }
}

if (stage != 1)
{
    Console.Error.WriteLine("only stage 1 exists");
    return 1;
}

var options = new GenerateOptions(Stage: stage, Size: size);
int failures = 0;
foreach (var (name, failure) in GeneratorSelfTests.Run(options))
{
    if (selfTest)
        Console.WriteLine(failure is null ? $"ok {name}" : $"FAIL {name}: {failure}");
    failures += failure is null ? 0 : 1;
}
if (failures > 0)
{
    Console.Error.WriteLine($"self-check failed ({failures})");
    return 1;
}
if (!selfTest)
    Console.Error.WriteLine("self-check ok");
else
    return 0;

var generator = new ProgramGenerator(options);
var directory = Path.Combine(Repository.Root(), "tests", ".out", "generate", $"seed{seed}");
Directory.CreateDirectory(directory);
for (int index = 0; index < max; index++)
{
    var program = generator.Generate(seed, index);
    var path = Path.Combine(directory, $"p{index}.wyl");
    File.WriteAllText(path, program.Text);
    if (max == 1)
        Console.Write(program.Text);
    else
        Console.WriteLine($"written to {path}");
}
return 0;
