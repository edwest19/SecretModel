// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using SecretModel.Core.Text;

namespace SecretModel.Core.Features;

/// <summary>
/// Turns one transaction's features into an observation symbol 0–511 (README §5, SM-QNT):
/// <c>symbol = ((amountBin * 8 + mccGroup) * 4 + timeBucket) * 2 + isOnline</c>.
/// Matches the packing in reference/python/client/quantizer.py; golden vectors check that
/// (SM-QNT-006). Turning an amount into an amount bin is not covered here (README OPEN-7).
/// </summary>
public static class Quantizer
{
    public const int AmountBinCount = 8;
    public const int MccGroupCount = 8;
    public const int TimeBucketCount = 4;
    public const int SymbolCount = AmountBinCount * MccGroupCount * TimeBucketCount * 2;

    /// <summary>The group for any category not in the table (SM-QNT-004).</summary>
    public const int OtherMccGroup = 7;

    /// <summary>
    /// The eight category names, lowercase, in group order: group g is <c>CategoryNames[g]</c>. A
    /// journal record must use one of these exactly (docs/formats/journal-v1.md).
    /// </summary>
    public static IReadOnlyList<string> CategoryNames { get; } =
        ["grocery", "gas", "restaurant", "retail", "travel", "online_market", "atm", "other"];

    private static readonly Dictionary<string, int> MccGroups = new(StringComparer.Ordinal)
    {
        ["grocery"] = 0,
        ["gas"] = 1,
        ["restaurant"] = 2,
        ["retail"] = 3,
        ["travel"] = 4,
        ["online_market"] = 5,
        ["atm"] = 6,
        ["other"] = 7,
    };

    /// <summary>
    /// The merchant-category group for a category name, matched after lowercasing. Unknown names,
    /// including the empty string, give group 7 (<c>other</c>) and never throw (SM-QNT-004).
    /// </summary>
    public static int MccGroup(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return MccGroups.TryGetValue(ReferenceText.Lower(category), out int group) ? group : OtherMccGroup;
    }

    /// <summary>
    /// The time bucket for an hour of the day (SM-QNT-003): 0–5 → 0, 6–11 → 1, 12–17 → 2,
    /// 18–23 → 3. Hours outside 0–23 are rejected.
    /// </summary>
    public static int TimeBucket(int hour)
    {
        if (hour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(hour), hour, "The hour must be 0 to 23.");
        }

        return hour < 6 ? 0 : hour < 12 ? 1 : hour < 18 ? 2 : 3;
    }

    /// <summary>
    /// Packs the four features into a symbol (SM-QNT-001). Each feature must be in range; nothing
    /// is clamped (SM-QNT-005).
    /// </summary>
    public static int Pack(int amountBin, int mccGroup, int timeBucket, bool isOnline)
    {
        CheckRange(amountBin, AmountBinCount, nameof(amountBin));
        CheckRange(mccGroup, MccGroupCount, nameof(mccGroup));
        CheckRange(timeBucket, TimeBucketCount, nameof(timeBucket));
        return ((amountBin * MccGroupCount + mccGroup) * TimeBucketCount + timeBucket) * 2 + (isOnline ? 1 : 0);
    }

    /// <summary>The symbol for an amount bin, category name, hour of day and online flag.</summary>
    public static int ToSymbol(int amountBin, string category, int hour, bool isOnline) =>
        Pack(amountBin, MccGroup(category), TimeBucket(hour), isOnline);

    private static void CheckRange(int value, int count, string name)
    {
        if (value < 0 || value >= count)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Must be 0 to {count - 1}.");
        }
    }
}
