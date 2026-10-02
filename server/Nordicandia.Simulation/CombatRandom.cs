namespace Nordicandia.Simulation;

/// <summary>
/// Deterministic, dependency-free random source so combat samples are reproducible.
/// Uses SplitMix64; identical seeds and call order always produce identical results.
/// </summary>
public sealed class CombatRandom
{
    private ulong state;

    public CombatRandom(ulong seed) => state = seed;

    public ulong NextUInt64()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}
