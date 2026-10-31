namespace Wyl.Generate;

/// <summary>
/// SplitMix64: one seed gives the same programs in every process and on every machine, with no
/// <c>System.Random</c> and no dependence on hash order. Generation happens before any parallel
/// checking, so nothing after it can change a program.
/// </summary>
sealed class Rng(ulong state)
{
    const ulong Gamma = 0x9E3779B97F4A7C15;

    /// <summary>The stream for one program: the run's seed and the program's index, mixed apart.</summary>
    public static Rng Derive(int seed, int index) => new(Mix(((ulong)(uint)seed << 32) | (uint)index));

    public ulong NextU64()
    {
        state += Gamma;
        return Mix(state);
    }

    /// <summary>A value in <c>[0, maxExclusive)</c>, rejecting the top of the range so no value is likelier.</summary>
    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        var bound = (ulong)maxExclusive;
        var limit = ulong.MaxValue - ulong.MaxValue % bound;
        ulong value;
        do value = NextU64();
        while (value >= limit);
        return (int)(value % bound);
    }

    public bool Chance(int percent) => Next(100) < percent;

    public bool NextBool() => Next(2) == 0;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>A copy in a random order: how a constructor table tries every way it knows.</summary>
    public List<T> Permute<T>(IReadOnlyList<T> items)
    {
        var copy = new List<T>(items);
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
        return value ^ (value >> 31);
    }
}
