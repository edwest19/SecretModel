// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

namespace SecretModel.Core.Hmm;

/// <summary>The most likely state path for an observation sequence and its log probability.</summary>
public sealed record ViterbiResult(int[] Path, double LogProbability);

/// <summary>
/// The outcome of <see cref="HiddenMarkovModel.Train"/>. <see cref="LogLikelihoods"/> holds the total
/// log-likelihood of the training sequences under the starting model, then under the model after
/// each update; the last entry belongs to <see cref="Model"/>.
/// </summary>
public sealed record HmmTrainingResult(
    HiddenMarkovModel Model, IReadOnlyList<double> LogLikelihoods, int Iterations, bool Converged);

/// <summary>
/// A discrete Hidden Markov Model (README §6, SM-HMM): initial probabilities pi, transitions a and
/// emissions b. Immutable; training returns a new model.
/// </summary>
public sealed class HiddenMarkovModel
{
    public const int DefaultStateCount = 5;
    public const int DefaultSymbolCount = 512;

    /// <summary>How far a probability row may be from summing to 1 (SM-HMM-006).</summary>
    public const double RowSumTolerance = 1e-9;

    private readonly double[] pi; // [state]
    private readonly double[] a;  // [from * StateCount + to]
    private readonly double[] b;  // [state * SymbolCount + symbol]

    /// <summary>Builds a model from its parameters, copying them. Every row must be a distribution.</summary>
    public HiddenMarkovModel(double[] initial, double[,] transitions, double[,] emissions)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(emissions);
        int n = initial.Length;
        if (n == 0 || transitions.GetLength(0) != n || transitions.GetLength(1) != n || emissions.GetLength(0) != n)
        {
            throw new ArgumentException("pi, a and b must all have the same, non-zero number of states.");
        }

        int m = emissions.GetLength(1);
        if (m == 0)
        {
            throw new ArgumentException("b must have at least one symbol.", nameof(emissions));
        }

        StateCount = n;
        SymbolCount = m;
        pi = (double[])initial.Clone();
        a = new double[n * n];
        b = new double[n * m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                a[i * n + j] = transitions[i, j];
            }

            for (int k = 0; k < m; k++)
            {
                b[i * m + k] = emissions[i, k];
            }
        }

        Validate();
    }

    private HiddenMarkovModel(int n, int m, double[] pi, double[] a, double[] b)
    {
        StateCount = n;
        SymbolCount = m;
        this.pi = pi;
        this.a = a;
        this.b = b;
        Validate();
    }

    public int StateCount { get; }

    public int SymbolCount { get; }

    public double Initial(int state) => pi[CheckIndex(state, StateCount, nameof(state))];

    public double Transition(int from, int to) =>
        a[CheckIndex(from, StateCount, nameof(from)) * StateCount + CheckIndex(to, StateCount, nameof(to))];

    public double Emission(int state, int symbol) =>
        b[CheckIndex(state, StateCount, nameof(state)) * SymbolCount + CheckIndex(symbol, SymbolCount, nameof(symbol))];

    /// <summary>
    /// A model with strictly positive random rows drawn from <see cref="SplitMix64"/> (SM-HMM-004,
    /// SM-HMM-008). The same seed gives bit-identical parameters on every platform: only integer
    /// arithmetic and division are involved.
    /// </summary>
    public static HiddenMarkovModel CreateRandom(
        ulong seed, int stateCount = DefaultStateCount, int symbolCount = DefaultSymbolCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stateCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 1);
        var rng = new SplitMix64(seed);
        double[] pi = RandomRows(rng, 1, stateCount);
        double[] a = RandomRows(rng, stateCount, stateCount);
        double[] b = RandomRows(rng, stateCount, symbolCount);
        return new HiddenMarkovModel(stateCount, symbolCount, pi, a, b);
    }

    /// <summary>
    /// log P(observations | model), by the forward algorithm with per-step scaling (SM-HMM-001), so
    /// long sequences don't underflow (SM-HMM-007). A sequence the model can't produce gives
    /// negative infinity, never NaN.
    /// </summary>
    public double LogLikelihood(int[] observations)
    {
        CheckObservations(observations, nameof(observations));
        return Forward(observations, new double[observations.Length * StateCount], new double[observations.Length]);
    }

    /// <summary>The most likely state path, in log space (SM-HMM-003). Ties go to the lower state.</summary>
    public ViterbiResult Viterbi(int[] observations)
    {
        CheckObservations(observations, nameof(observations));
        int n = StateCount;
        int length = observations.Length;
        var delta = new double[n];
        var next = new double[n];
        var back = new int[length * n];

        for (int i = 0; i < n; i++)
        {
            delta[i] = Math.Log(pi[i]) + Math.Log(b[i * SymbolCount + observations[0]]);
        }

        for (int t = 1; t < length; t++)
        {
            for (int j = 0; j < n; j++)
            {
                int best = 0;
                double bestScore = delta[0] + Math.Log(a[j]);
                for (int i = 1; i < n; i++)
                {
                    double score = delta[i] + Math.Log(a[i * n + j]);
                    if (score > bestScore)
                    {
                        best = i;
                        bestScore = score;
                    }
                }

                back[t * n + j] = best;
                next[j] = bestScore + Math.Log(b[j * SymbolCount + observations[t]]);
            }

            (delta, next) = (next, delta);
        }

        int last = 0;
        for (int i = 1; i < n; i++)
        {
            if (delta[i] > delta[last])
            {
                last = i;
            }
        }

        var path = new int[length];
        path[length - 1] = last;
        for (int t = length - 1; t > 0; t--)
        {
            path[t - 1] = back[t * n + path[t]];
        }

        return new ViterbiResult(path, delta[last]);
    }

    /// <summary>
    /// Maximum-likelihood Baum-Welch over one or more sequences (README §6). Stops after
    /// <paramref name="maxIterations"/> updates, or earlier when an update raises the total
    /// log-likelihood by less than <paramref name="tolerance"/>. Each update can't lower the
    /// log-likelihood (SM-HMM-002); every row stays a distribution (SM-HMM-006). A state that
    /// expects no visits keeps its previous row rather than dividing by zero.
    /// </summary>
    public HmmTrainingResult Train(IReadOnlyList<int[]> sequences, int maxIterations = 50, double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        if (sequences.Count == 0)
        {
            throw new ArgumentException("At least one training sequence is required.", nameof(sequences));
        }

        foreach (int[] sequence in sequences)
        {
            CheckObservations(sequence, nameof(sequences));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance must be finite and not negative.");
        }

        HiddenMarkovModel model = this;
        var logLikelihoods = new List<double>();
        for (int iteration = 0; ; iteration++)
        {
            Counts counts = model.ExpectedCounts(sequences);
            if (double.IsNegativeInfinity(counts.LogLikelihood))
            {
                throw new InvalidOperationException("A training sequence has probability zero under the current model.");
            }

            logLikelihoods.Add(counts.LogLikelihood);
            if (iteration > 0 && logLikelihoods[^1] - logLikelihoods[^2] < tolerance)
            {
                return new HmmTrainingResult(model, logLikelihoods, iteration, Converged: true);
            }

            if (iteration == maxIterations)
            {
                return new HmmTrainingResult(model, logLikelihoods, iteration, Converged: false);
            }

            model = model.Reestimate(counts);
        }
    }

    internal ReadOnlySpan<double> InitialRow => pi;

    internal ReadOnlySpan<double> TransitionRow(int from) => a.AsSpan(from * StateCount, StateCount);

    internal ReadOnlySpan<double> EmissionRow(int state) => b.AsSpan(state * SymbolCount, SymbolCount);

    private sealed record Counts(double[] Pi, double[] A, double[] B, double LogLikelihood);

    private Counts ExpectedCounts(IReadOnlyList<int[]> sequences)
    {
        int n = StateCount;
        int m = SymbolCount;
        var piCount = new double[n];
        var aCount = new double[n * n];
        var bCount = new double[n * m];
        double total = 0;

        foreach (int[] obs in sequences)
        {
            int length = obs.Length;
            var alpha = new double[length * n];
            var scale = new double[length];
            double logLikelihood = Forward(obs, alpha, scale);
            if (double.IsNegativeInfinity(logLikelihood))
            {
                return new Counts(piCount, aCount, bCount, double.NegativeInfinity);
            }

            total += logLikelihood;

            // Scaled backward pass: beta[T-1] = 1, beta[t](i) = sum_j a(i,j) b_j(o[t+1]) beta[t+1](j) / c[t+1].
            var beta = new double[length * n];
            for (int i = 0; i < n; i++)
            {
                beta[(length - 1) * n + i] = 1.0;
            }

            for (int t = length - 2; t >= 0; t--)
            {
                int o = obs[t + 1];
                for (int i = 0; i < n; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < n; j++)
                    {
                        sum += a[i * n + j] * b[j * m + o] * beta[(t + 1) * n + j];
                    }

                    beta[t * n + i] = sum / scale[t + 1];
                }
            }

            // gamma[t](i) = alpha[t](i) beta[t](i); xi[t](i,j) = alpha[t](i) a(i,j) b_j(o[t+1]) beta[t+1](j) / c[t+1].
            for (int t = 0; t < length; t++)
            {
                for (int i = 0; i < n; i++)
                {
                    double gamma = alpha[t * n + i] * beta[t * n + i];
                    if (t == 0)
                    {
                        piCount[i] += gamma;
                    }

                    bCount[i * m + obs[t]] += gamma;
                }

                if (t < length - 1)
                {
                    int o = obs[t + 1];
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            aCount[i * n + j] += alpha[t * n + i] * a[i * n + j] * b[j * m + o] * beta[(t + 1) * n + j] / scale[t + 1];
                        }
                    }
                }
            }
        }

        return new Counts(piCount, aCount, bCount, total);
    }

    private HiddenMarkovModel Reestimate(Counts counts)
    {
        int n = StateCount;
        int m = SymbolCount;
        return new HiddenMarkovModel(
            n,
            m,
            NormalizeRows(counts.Pi, pi, 1, n),
            NormalizeRows(counts.A, a, n, n),
            NormalizeRows(counts.B, b, n, m));
    }

    // Divides each row of counts by its sum. A row with no expected visits keeps the previous row.
    private static double[] NormalizeRows(double[] counts, double[] previous, int rows, int columns)
    {
        var result = new double[counts.Length];
        for (int r = 0; r < rows; r++)
        {
            double sum = 0;
            for (int c = 0; c < columns; c++)
            {
                sum += counts[r * columns + c];
            }

            for (int c = 0; c < columns; c++)
            {
                result[r * columns + c] = sum > 0 ? counts[r * columns + c] / sum : previous[r * columns + c];
            }
        }

        return result;
    }

    // Scaled forward pass (Rabiner 1989): alpha[t] is normalized to sum 1 and c[t] keeps the factor,
    // so log P(O) = sum of log c[t]. Returns negative infinity when some c[t] is 0.
    private double Forward(int[] obs, double[] alpha, double[] scale)
    {
        int n = StateCount;
        int m = SymbolCount;
        double logLikelihood = 0;
        for (int t = 0; t < obs.Length; t++)
        {
            int o = obs[t];
            double c = 0;
            for (int j = 0; j < n; j++)
            {
                double sum;
                if (t == 0)
                {
                    sum = pi[j];
                }
                else
                {
                    sum = 0;
                    for (int i = 0; i < n; i++)
                    {
                        sum += alpha[(t - 1) * n + i] * a[i * n + j];
                    }
                }

                double value = sum * b[j * m + o];
                alpha[t * n + j] = value;
                c += value;
            }

            if (c == 0)
            {
                return double.NegativeInfinity;
            }

            for (int j = 0; j < n; j++)
            {
                alpha[t * n + j] /= c;
            }

            scale[t] = c;
            logLikelihood += Math.Log(c);
        }

        return logLikelihood;
    }

    private static double[] RandomRows(SplitMix64 rng, int rows, int columns)
    {
        var values = new double[rows * columns];
        for (int r = 0; r < rows; r++)
        {
            double sum = 0;
            for (int c = 0; c < columns; c++)
            {
                values[r * columns + c] = rng.NextUnitInterval();
                sum += values[r * columns + c];
            }

            for (int c = 0; c < columns; c++)
            {
                values[r * columns + c] /= sum;
            }
        }

        return values;
    }

    private void Validate()
    {
        CheckRows(pi, 1, StateCount, "pi");
        CheckRows(a, StateCount, StateCount, "a");
        CheckRows(b, StateCount, SymbolCount, "b");
    }

    private static void CheckRows(double[] values, int rows, int columns, string name)
    {
        for (int r = 0; r < rows; r++)
        {
            double sum = 0;
            for (int c = 0; c < columns; c++)
            {
                double v = values[r * columns + c];
                if (!double.IsFinite(v) || v < 0)
                {
                    throw new ArgumentException($"{name} row {r} has a value that is negative or not finite.");
                }

                sum += v;
            }

            if (Math.Abs(sum - 1.0) > RowSumTolerance)
            {
                throw new ArgumentException($"{name} row {r} sums to {sum:R}, not 1.");
            }
        }
    }

    private void CheckObservations(int[] observations, string name)
    {
        ArgumentNullException.ThrowIfNull(observations, name);
        if (observations.Length == 0)
        {
            throw new ArgumentException("An observation sequence must not be empty.", name);
        }

        foreach (int symbol in observations)
        {
            if (symbol < 0 || symbol >= SymbolCount)
            {
                throw new ArgumentOutOfRangeException(name, symbol, $"Symbols must be 0 to {SymbolCount - 1}.");
            }
        }
    }

    private static int CheckIndex(int value, int count, string name) =>
        value >= 0 && value < count ? value : throw new ArgumentOutOfRangeException(name, value, $"Must be 0 to {count - 1}.");
}
