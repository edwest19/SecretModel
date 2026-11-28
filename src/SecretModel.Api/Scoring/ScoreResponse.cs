// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 9, decision 2, and the Task 4 answers). AI-generated.

using System.Text.Json;
using SecretModel.Core.Scoring;

namespace SecretModel.Api.Scoring;

/// <summary>
/// Writes the body of a <c>200</c> answer to <c>POST /v1/score</c>, format score response version 1
/// (docs/formats/score-response-v1.md): nine properties in a fixed order, numbers as
/// <see cref="Utf8JsonWriter"/> writes a <see cref="double"/>.
/// </summary>
public static class ScoreResponse
{
    /// <summary>The answer's body. Throws <see cref="ArgumentException"/> for a number that isn't finite, as the writer does.</summary>
    public static byte[] Write(string transactionToken, ScoreResult result)
    {
        ArgumentNullException.ThrowIfNull(transactionToken);
        ArgumentNullException.ThrowIfNull(result);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("transaction_token", transactionToken);
            writer.WriteNumber("fraud_score", result.FraudScore);
            writer.WriteString("verdict", VerdictName(result.Verdict));
            writer.WriteNumber("confidence", result.Confidence);
            writer.WriteStartArray("anomaly_factors");
            foreach (string factor in result.AnomalyFactors)
            {
                writer.WriteStringValue(factor);
            }

            writer.WriteEndArray();
            WriteNumberOrNull(writer, "window_log_likelihood", result.WindowLogLikelihood);
            WriteNumberOrNull(writer, "baseline_tail", result.BaselineTail);
            if (result.ExpectedLow is { } low && result.ExpectedHigh is { } high)
            {
                writer.WriteStartArray("expected_range");
                writer.WriteNumberValue(low);
                writer.WriteNumberValue(high);
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNull("expected_range");
            }

            writer.WriteStartArray("viterbi_path");
            foreach (int state in result.ViterbiPath)
            {
                writer.WriteNumberValue(state);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary><c>LEGIT</c>, <c>REVIEW</c> or <c>FRAUD_SUSPECTED</c> (README SM-SCORE-006).</summary>
    public static string VerdictName(Verdict verdict) => verdict switch
    {
        Verdict.Legit => "LEGIT",
        Verdict.Review => "REVIEW",
        Verdict.FraudSuspected => "FRAUD_SUSPECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
