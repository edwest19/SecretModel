// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

namespace SecretModel.Core.Hmm;

/// <summary>
/// SplitMix64 (Steele, Lea and Flood; reference code http://xoshiro.di.unimi.it/splitmix64.c), used
/// to seed HMM initial parameters. Unlike System.Random, its output is fixed by its definition, so
/// a seed gives the same model on every OS and .NET version (README SM-HMM-004). It uses only
/// integer arithmetic. Not for cryptography.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong state = seed;

    public ulong NextUInt64()
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A double in (0, 1]: the top 53 bits plus one, times 2^-53. Both steps are exact.</summary>
    public double NextUnitInterval() => ((NextUInt64() >> 11) + 1) * (1.0 / 9007199254740992.0);
}
