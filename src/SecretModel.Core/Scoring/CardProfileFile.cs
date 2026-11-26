// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using System.Text.Json;
using SecretModel.Core.Hmm;

namespace SecretModel.Core.Scoring;

/// <summary>
/// Reads and writes the card profile file, format "secretmodel-profile" version 1
/// (docs/formats/profile-v1.md). The trained model and its likelihood history (the baseline) are
/// stored together in one file (README SM-STOR-003). Numbers round-trip bit for bit; the reader is
/// strict and rejects anything else with <see cref="InvalidDataException"/>.
/// </summary>
public static class CardProfileFile
{
    public const string Format = "secretmodel-profile";
    public const int Version = 1;

    private static readonly string[] PropertyOrder =
        ["format", "version", "window", "history", "recent", "state_before", "seen", "baseline", "model"];

    /// <summary>The profile as UTF-8 JSON: one line, no byte-order mark, no trailing newline.</summary>
    public static byte[] Write(CardProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WriteNumber("window", profile.WindowLength);
            writer.WriteNumber("history", profile.HistoryLength);
            WriteInts(writer, "recent", profile.RecentSymbols);
            writer.WritePropertyName("state_before");
            HmmModelFile.WriteRow(writer, [.. profile.StateBeforeWindow]);
            WriteInts(writer, "seen", profile.SeenSymbols);
            writer.WritePropertyName("baseline");
            HmmModelFile.WriteRow(writer, [.. profile.Baseline]);
            writer.WritePropertyName("model");
            HmmModelFile.WriteObject(writer, profile.Model);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Reads a profile written by <see cref="Write"/>, or any file that follows the format.</summary>
    public static CardProfile Read(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The profile file is not valid JSON.", e);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The profile file must hold a JSON object.");
            }

            string[] names = [.. root.EnumerateObject().Select(p => p.Name)];
            if (!names.SequenceEqual(PropertyOrder))
            {
                throw new InvalidDataException(
                    $"The profile file must have exactly these properties, in this order: {string.Join(", ", PropertyOrder)}.");
            }

            if (root.GetProperty("format").ValueKind != JsonValueKind.String || root.GetProperty("format").GetString() != Format)
            {
                throw new InvalidDataException($"The format must be \"{Format}\".");
            }

            if (HmmModelFile.ReadCount(root, "version") != Version)
            {
                throw new InvalidDataException($"Only version {Version} is supported.");
            }

            int window = HmmModelFile.ReadCount(root, "window");
            int history = HmmModelFile.ReadCount(root, "history");
            HiddenMarkovModel model = HmmModelFile.ReadObject(root.GetProperty("model"));
            int[] recent = ReadInts(root.GetProperty("recent"), "recent");
            double[] stateBefore = HmmModelFile.ReadRow(root.GetProperty("state_before"), model.StateCount, "state_before");
            int[] seen = ReadInts(root.GetProperty("seen"), "seen");
            JsonElement baselineElement = root.GetProperty("baseline");
            if (baselineElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("\"baseline\" must be an array of numbers.");
            }

            double[] baseline = HmmModelFile.ReadRow(baselineElement, baselineElement.GetArrayLength(), "baseline");
            try
            {
                return CardProfile.FromParts(model, window, history, recent, stateBefore, seen, baseline);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"The profile file holds an invalid profile: {e.Message}", e);
            }
        }
    }

    private static void WriteInts(Utf8JsonWriter writer, string name, IReadOnlyList<int> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (int v in values)
        {
            writer.WriteNumberValue(v);
        }

        writer.WriteEndArray();
    }

    private static int[] ReadInts(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"\"{name}\" must be an array of whole numbers.");
        }

        var values = new int[e.GetArrayLength()];
        int k = 0;
        foreach (JsonElement item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int v))
            {
                throw new InvalidDataException($"\"{name}\" item {k} must be a whole number.");
            }

            values[k++] = v;
        }

        return values;
    }
}
