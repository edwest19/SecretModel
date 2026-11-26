// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// The measurement behind README §7.1 (OPEN-2, OPEN-3). For each setting it builds profiles for 10
// synthetic shoppers, scores 300 later habitual purchases and 100 of each anomaly kind, and reports
// the AUC of fraud_score (ties count half) and how often each verdict comes out.
//
// Run from the repository root:   dotnet run -c Release --project tools/SecretModel.ScoringStudy
// Optional arguments set the defaults used by the later sections: W λ backoff μ α h
//   e.g.  dotnet run -c Release --project tools/SecretModel.ScoringStudy -- 1 0.3 Features 0.01 0.05 0.33

using System.Globalization;
using SecretModel.Core.Scoring;
using SecretModel.Synthetic;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const int Shoppers = 10;
const int NormalCount = 300;
const int AnomaliesPerKind = 100;
AnomalyKind[] kinds = [AnomalyKind.Random, AnomalyKind.Takeover, AnomalyKind.Subtle];

var chosen = new ScoringOptions
{
    WindowLength = args.Length > 0 ? int.Parse(args[0]) : new ScoringOptions().WindowLength,
    EmissionSmoothing = args.Length > 1 ? double.Parse(args[1]) : new ScoringOptions().EmissionSmoothing,
    Backoff = args.Length > 2 ? Enum.Parse<EmissionBackoff>(args[2]) : new ScoringOptions().Backoff,
    FeatureFloor = args.Length > 3 ? double.Parse(args[3]) : new ScoringOptions().FeatureFloor,
    TailFraction = args.Length > 4 ? double.Parse(args[4]) : new ScoringOptions().TailFraction,
    HoldoutFraction = args.Length > 5 ? double.Parse(args[5]) : new ScoringOptions().HoldoutFraction,
};

Section("1. Smoothing toward uniform vs toward per-feature habits (W 1, 5, 10; history 300; baseline in-sample, h 0; α 0.05)");
foreach (int w in new[] { 1, 5, 10 })
{
    foreach (double lambda in new[] { 0.0, 0.01, 0.1 })
    {
        Row($"W={w} uniform λ={lambda}", Evaluate(new ScoringOptions
        {
            WindowLength = w, EmissionSmoothing = lambda, Backoff = EmissionBackoff.Uniform, HoldoutFraction = 0, TailFraction = 0.05,
        }, 300, false));
    }

    foreach (double lambda in new[] { 0.1, 0.3, 0.5, 0.8 })
    {
        foreach (double mu in new[] { 0.01, 0.05, 0.2 })
        {
            Row($"W={w} features λ={lambda} μ={mu}", Evaluate(new ScoringOptions
            {
                WindowLength = w, EmissionSmoothing = lambda, Backoff = EmissionBackoff.Features, FeatureFloor = mu, HoldoutFraction = 0, TailFraction = 0.05,
            }, 300, false));
        }
    }
}

Section("1b. Baseline held out of training (feature backoff μ 0.01; history 300 and 1000; α 0.05)");
foreach (int history in new[] { 300, 1000 })
{
    foreach (int w in new[] { 1, 5, 10 })
    {
        foreach (double lambda in new[] { 0.3, 0.5 })
        {
            foreach (double h in new[] { 0.0, 0.2, 0.33, 0.5 })
            {
                Row($"history={history} W={w} λ={lambda} h={h}", Evaluate(new ScoringOptions
                {
                    WindowLength = w, EmissionSmoothing = lambda, Backoff = EmissionBackoff.Features, FeatureFloor = 0.01, HoldoutFraction = h, TailFraction = 0.05,
                }, history, false));
            }
        }
    }
}

Section($"2. Window length W ({Describe(chosen)} otherwise)");
foreach (int w in new[] { 1, 2, 3, 5, 10, 20 })
{
    Row($"W={w}", Evaluate(chosen with { WindowLength = w }, 300, false));
}

Section($"3. History length ({Describe(chosen)})");
foreach (int history in new[] { 30, 60, 100, 300, 1000 })
{
    Row($"history={history}", Evaluate(chosen, history, false));
}

Section($"4. Tail fraction α ({Describe(chosen)}, history 300)");
foreach (double alpha in new[] { 0.01, 0.02, 0.05, 0.1 })
{
    Row($"α={alpha}", Evaluate(chosen with { TailFraction = alpha }, 300, false));
}

Section($"5. Context: journal tail (the read-only service) vs each purchase's true predecessors ({Describe(chosen)}, history 300)");
Row("journal tail (as built)", Evaluate(chosen, 300, false));
Row("true predecessors (diagnostic)", Evaluate(chosen, 300, true));

Summary Evaluate(ScoringOptions options, int historyLength, bool trueContext)
{
    // Every kind has the same number of anomalies, so the mean of the per-kind AUCs equals the AUC of
    // all anomalies pooled against the same normal purchases.
    var perKind = kinds.ToDictionary(k => k, _ => new List<double>());
    var normalVerdicts = new int[3];
    var anomalyVerdicts = new int[3];
    for (ulong seed = 1; seed <= Shoppers; seed++)
    {
        var shopper = new SyntheticShopper(seed);
        int[] history = shopper.History(historyLength);
        CardProfile profile = CardProfile.Build(history, options);
        int[] future = [.. Enumerable.Range(0, NormalCount).Select(_ => shopper.Next().Symbol)];
        var context = trueContext ? new TrueContext(profile, options, history, future) : null;

        double[] normal = [.. future.Select((s, i) => Score(profile, options, context, i, s, normalVerdicts))];
        foreach (AnomalyKind kind in kinds)
        {
            double[] anomalous = [.. Enumerable.Range(0, AnomaliesPerKind)
                .Select(i => Score(profile, options, context, i, shopper.Anomaly(kind).Symbol, anomalyVerdicts))];
            perKind[kind].Add(Auc(normal, anomalous));
        }
    }

    return new Summary(
        kinds.Select(k => perKind[k].Average()).Average(),
        perKind[AnomalyKind.Random].Average(),
        perKind[AnomalyKind.Takeover].Average(),
        perKind[AnomalyKind.Subtle].Average(),
        Share(normalVerdicts, 1), Share(normalVerdicts, 2), Share(anomalyVerdicts, 1), Share(anomalyVerdicts, 2));
}

static double Score(CardProfile profile, ScoringOptions options, TrueContext? context, int position, int symbol, int[] verdicts)
{
    double fraudScore = context is null
        ? FraudScorer.Score(profile, symbol, options).FraudScore
        : context.Score(position, symbol);
    verdicts[(int)FraudScorer.Classify(fraudScore, options)]++;
    return fraudScore;
}

// Probability that a random anomalous purchase scores higher than a random normal one; ties count half.
static double Auc(double[] normal, double[] anomalous)
{
    double wins = 0;
    foreach (double a in anomalous)
    {
        foreach (double n in normal)
        {
            wins += a > n ? 1 : a == n ? 0.5 : 0;
        }
    }

    return wins / (anomalous.Length * (double)normal.Length);
}

static double Share(int[] verdicts, int atLeast) => verdicts.Skip(atLeast).Sum() / (double)verdicts.Sum();

static string Describe(ScoringOptions o) =>
    $"W {o.WindowLength}, {o.Backoff.ToString().ToLowerInvariant()} λ {o.EmissionSmoothing}" +
    (o.Backoff == EmissionBackoff.Features ? $" μ {o.FeatureFloor}" : "") + $", α {o.TailFraction}, h {o.HoldoutFraction}";

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"## {title}");
    Console.WriteLine();
    Console.WriteLine("| Setting | AUC (mean of kinds) | AUC random | AUC takeover | AUC subtle | Normal REVIEW+ | Normal FRAUD | Anomaly REVIEW+ | Anomaly FRAUD |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
}

static void Row(string label, Summary s) =>
    Console.WriteLine($"| {label} | {s.Auc:F3} | {s.Random:F3} | {s.Takeover:F3} | {s.Subtle:F3} | {s.NormalFlagged:P1} | {s.NormalFraud:P1} | {s.AnomalyFlagged:P1} | {s.AnomalyFraud:P1} |");

internal sealed record Summary(
    double Auc, double Random, double Takeover, double Subtle,
    double NormalFlagged, double NormalFraud, double AnomalyFlagged, double AnomalyFraud);

// Diagnostic only: scores purchase number i of the future against the history plus the i future
// purchases before it, which the read-only service never sees. Same model and baseline as the profile.
internal sealed class TrueContext
{
    private readonly CardProfile profile;
    private readonly ScoringOptions options;
    private readonly int[] sequence;
    private readonly int historyLength;
    private readonly double[]?[] stateAfter; // stateAfter[k]: hidden state after the first k symbols

    public TrueContext(CardProfile profile, ScoringOptions options, int[] history, int[] future)
    {
        this.profile = profile;
        this.options = options;
        historyLength = history.Length;
        sequence = [.. history, .. future];
        stateAfter = new double[]?[sequence.Length + 1];
        for (int k = 0; k < sequence.Length; k++)
        {
            stateAfter[k + 1] = profile.Model.FilterState([sequence[k]], stateAfter[k]);
        }
    }

    public double Score(int position, int symbol)
    {
        int at = historyLength + position;
        int w = options.WindowLength;
        int[] window = [.. sequence[(at - w + 1)..at], symbol];
        double perSymbol = profile.Model.StepLogLikelihoods(window, stateAfter[at - w + 1]).Sum() / w;
        double tail = FraudScorer.BaselineTail(profile.Baseline, perSymbol);
        return Math.Clamp(1 - tail / options.TailFraction, 0, 1);
    }
}
