// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using SecretModel.Core.Features;
using SecretModel.Core.Hmm;

namespace SecretModel.Core.Scoring;

/// <summary>
/// Everything needed to score one card's transactions (README §7): its trained HMM, the window length
/// W it was built for, the baseline (the per-symbol log-likelihood of every length-W window of its
/// history, each conditioned on the history before it, sorted), the last W − 1 history symbols, and
/// the hidden-state distribution just before them. Built from the journal only; scoring never
/// changes it.
/// </summary>
public sealed class CardProfile
{
    /// <summary>A card with fewer journal transactions than this stays cold-start (SM-JRNL-007).</summary>
    public const int MinimumHistory = 10;

    private readonly double[] baseline;
    private readonly int[] recentSymbols;
    private readonly double[] stateBeforeWindow;
    private readonly bool[] seenSymbols;

    private CardProfile(
        HiddenMarkovModel model,
        int windowLength,
        double[] baseline,
        int[] recentSymbols,
        double[] stateBeforeWindow,
        bool[] seenSymbols,
        int historyLength)
    {
        Model = model;
        WindowLength = windowLength;
        this.baseline = baseline;
        this.recentSymbols = recentSymbols;
        this.stateBeforeWindow = stateBeforeWindow;
        this.seenSymbols = seenSymbols;
        HistoryLength = historyLength;
    }

    public HiddenMarkovModel Model { get; }

    /// <summary>W, fixed when the profile is built; scoring always uses this length (SM-SCORE-003).</summary>
    public int WindowLength { get; }

    /// <summary>How many journal transactions the profile was built from.</summary>
    public int HistoryLength { get; }

    /// <summary>Per-symbol log-likelihood of each length-W window of the history, ascending.</summary>
    public IReadOnlyList<double> Baseline => baseline;

    /// <summary>The last W − 1 history symbols; a scored window is these plus the new one.</summary>
    public IReadOnlyList<int> RecentSymbols => recentSymbols;

    /// <summary>
    /// P(hidden state | every history symbol before <see cref="RecentSymbols"/>): the context a scored
    /// window is conditioned on, exactly as each baseline window is conditioned on the history before it.
    /// </summary>
    public IReadOnlyList<double> StateBeforeWindow => stateBeforeWindow;

    internal double[] StateBeforeWindowArray => stateBeforeWindow;

    /// <summary>True if the symbol occurs anywhere in the history.</summary>
    public bool HasSeen(int symbol) => symbol >= 0 && symbol < seenSymbols.Length && seenSymbols[symbol];

    /// <summary>Every symbol that occurs in the history, ascending.</summary>
    public IReadOnlyList<int> SeenSymbols => [.. Enumerable.Range(0, seenSymbols.Length).Where(s => seenSymbols[s])];

    /// <summary>
    /// Rebuilds a profile from stored parts (<see cref="CardProfileFile"/>), checking that they fit
    /// together. Throws <see cref="ArgumentException"/> when they don't.
    /// </summary>
    internal static CardProfile FromParts(
        HiddenMarkovModel model, int windowLength, int historyLength, int[] recent, double[] stateBefore, int[] seen, double[] baseline)
    {
        int m = model.SymbolCount;
        if (windowLength < 1 || historyLength < windowLength)
        {
            throw new ArgumentException("The window must be at least 1 and no longer than the history.");
        }

        if (recent.Length != windowLength - 1 || recent.Any(s => s < 0 || s >= m))
        {
            throw new ArgumentException($"There must be exactly W − 1 = {windowLength - 1} recent symbol(s), each 0 to {m - 1}.");
        }

        if (stateBefore.Length != model.StateCount || stateBefore.Any(p => !double.IsFinite(p) || p < 0)
            || Math.Abs(stateBefore.Sum() - 1.0) > HiddenMarkovModel.RowSumTolerance)
        {
            throw new ArgumentException($"The state before the window must be a distribution over {model.StateCount} states.");
        }

        if (seen.Length == 0 || seen.Length > historyLength)
        {
            throw new ArgumentException("There must be from 1 to (history) seen symbols.");
        }

        var seenFlags = new bool[m];
        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i] < 0 || seen[i] >= m || (i > 0 && seen[i] <= seen[i - 1]))
            {
                throw new ArgumentException($"The seen symbols must be ascending, without repeats, each 0 to {m - 1}.");
            }

            seenFlags[seen[i]] = true;
        }

        if (recent.Any(s => !seenFlags[s]))
        {
            throw new ArgumentException("Every recent symbol must be among the seen symbols.");
        }

        if (baseline.Length == 0 || baseline.Length > historyLength - windowLength + 1 || baseline.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("The baseline must hold 1 to (history − window + 1) finite values.");
        }

        for (int i = 1; i < baseline.Length; i++)
        {
            if (baseline[i] < baseline[i - 1])
            {
                throw new ArgumentException("The baseline must be in ascending order.");
            }
        }

        return new CardProfile(
            model, windowLength, (double[])baseline.Clone(), (int[])recent.Clone(), (double[])stateBefore.Clone(), seenFlags, historyLength);
    }

    /// <summary>
    /// Trains the card's HMM on the older part of its journal history (oldest first, one sequence) and
    /// smooths the emissions. One forward pass over the whole history then gives
    /// log P(symbol | everything before it) for every step; the mean of each run of W consecutive steps
    /// whose newest step is in the held-out part (or anywhere, when nothing is held out) is one
    /// baseline window.
    /// </summary>
    public static CardProfile Build(int[] history, ScoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        int needed = MinimumHistoryFor(options);
        if (history.Length < needed)
        {
            throw new ArgumentException($"A profile with these options needs at least {needed} transactions.", nameof(history));
        }

        int trainLength = TrainingLength(history.Length, options.HoldoutFraction);
        HiddenMarkovModel start = HiddenMarkovModel.CreateRandom(options.Seed, options.StateCount, options.SymbolCount);
        HiddenMarkovModel trained = start.Train([history[..trainLength]], options.MaxIterations, options.Tolerance).Model;
        HiddenMarkovModel model = Smooth(trained, options);

        int w = options.WindowLength;
        double[] steps = model.StepLogLikelihoods(history);
        int firstEnd = trainLength < history.Length ? Math.Max(trainLength, w - 1) : w - 1;
        var baseline = new double[history.Length - firstEnd];
        for (int i = 0; i < baseline.Length; i++)
        {
            int end = firstEnd + i;
            double sum = 0;
            for (int t = end - w + 1; t <= end; t++)
            {
                sum += steps[t];
            }

            baseline[i] = sum / w;
        }

        Array.Sort(baseline);
        var seen = new bool[options.SymbolCount];
        foreach (int symbol in history)
        {
            seen[symbol] = true;
        }

        // A scored window is the last W - 1 history symbols plus the new one. It starts after
        // history[0 .. Length - W], which always holds at least one symbol because Length >= W.
        int windowStart = history.Length - (w - 1);
        double[] stateBefore = model.FilterState(history[..windowStart]);
        return new CardProfile(model, w, baseline, history[windowStart..], stateBefore, seen, history.Length);
    }

    /// <summary>
    /// The shortest history these options can build a profile from: at least W, and enough that
    /// training gets <see cref="MinimumHistory"/> transactions and at least one is held out.
    /// </summary>
    public static int MinimumHistoryFor(ScoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int length = Math.Max(MinimumHistory, options.WindowLength);
        while (TrainingLength(length, options.HoldoutFraction) < MinimumHistory
               || (options.HoldoutFraction > 0 && TrainingLength(length, options.HoldoutFraction) == length))
        {
            length++;
        }

        return length;
    }

    // The older part used for training: the history minus floor(h × length) held-out transactions.
    private static int TrainingLength(int historyLength, double holdout) =>
        historyLength - (int)Math.Floor(historyLength * holdout);

    // b' = (1 - λ) b + λ q for every emission row (OPEN-2); pi and a are kept as trained.
    internal static HiddenMarkovModel Smooth(HiddenMarkovModel model, ScoringOptions options)
    {
        double lambda = options.EmissionSmoothing;
        if (lambda == 0)
        {
            return model;
        }

        int n = model.StateCount;
        int m = model.SymbolCount;
        var pi = new double[n];
        var a = new double[n, n];
        var b = new double[n, m];
        for (int i = 0; i < n; i++)
        {
            pi[i] = model.Initial(i);
            for (int j = 0; j < n; j++)
            {
                a[i, j] = model.Transition(i, j);
            }

            double[] q = options.Backoff == EmissionBackoff.Features
                ? FeatureBackoff(model, i, options.FeatureFloor)
                : [.. Enumerable.Repeat(1.0 / m, m)];
            for (int k = 0; k < m; k++)
            {
                b[i, k] = (1 - lambda) * model.Emission(i, k) + lambda * q[k];
            }
        }

        return new HiddenMarkovModel(pi, a, b);
    }

    // q(symbol) = product over the four features of (1 - μ) m_f(value) + μ / size_f, where m_f is the
    // state's emission row summed over the other features. Each factor sums to 1, so q does too.
    internal static double[] FeatureBackoff(HiddenMarkovModel model, int state, double floor)
    {
        int[] sizes = [Quantizer.AmountBinCount, Quantizer.MccGroupCount, Quantizer.TimeBucketCount, 2];
        double[][] marginals = [.. sizes.Select(s => new double[s])];
        for (int symbol = 0; symbol < Quantizer.SymbolCount; symbol++)
        {
            double p = model.Emission(state, symbol);
            int[] features = Features(symbol);
            for (int f = 0; f < 4; f++)
            {
                marginals[f][features[f]] += p;
            }
        }

        var q = new double[Quantizer.SymbolCount];
        for (int symbol = 0; symbol < q.Length; symbol++)
        {
            int[] features = Features(symbol);
            double product = 1;
            for (int f = 0; f < 4; f++)
            {
                product *= (1 - floor) * marginals[f][features[f]] + floor / sizes[f];
            }

            q[symbol] = product;
        }

        return q;
    }

    // Inverse of Quantizer.Pack: amount bin, merchant group, time bucket, online.
    private static int[] Features(int symbol) => [symbol >> 6, (symbol >> 3) & 7, (symbol >> 1) & 3, symbol & 1];
}
