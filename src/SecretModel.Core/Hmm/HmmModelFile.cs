// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using System.Text.Json;

namespace SecretModel.Core.Hmm;

/// <summary>
/// Reads and writes the HMM model file, format "secretmodel-hmm" version 1 (docs/formats/hmm-model-v1.md).
/// Doubles are written in the shortest form that reads back to the same bits (SM-HMM-005). The reader
/// is strict: anything other than exactly the documented properties, or a model that fails
/// validation, is rejected with <see cref="InvalidDataException"/>.
/// </summary>
public static class HmmModelFile
{
    public const string Format = "secretmodel-hmm";
    public const int Version = 1;

    private static readonly string[] PropertyOrder = ["format", "version", "states", "symbols", "pi", "a", "b"];

    /// <summary>The model as UTF-8 JSON: one line, no byte-order mark, no trailing newline.</summary>
    public static byte[] Write(HiddenMarkovModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(writer, model);
        }

        return stream.ToArray();
    }

    /// <summary>Writes the model as one JSON object, so other formats can embed it unchanged.</summary>
    internal static void WriteObject(Utf8JsonWriter writer, HiddenMarkovModel model)
    {
        writer.WriteStartObject();
        writer.WriteString("format", Format);
        writer.WriteNumber("version", Version);
        writer.WriteNumber("states", model.StateCount);
        writer.WriteNumber("symbols", model.SymbolCount);
        writer.WritePropertyName("pi");
        WriteRow(writer, model.InitialRow);
        writer.WritePropertyName("a");
        writer.WriteStartArray();
        for (int i = 0; i < model.StateCount; i++)
        {
            WriteRow(writer, model.TransitionRow(i));
        }

        writer.WriteEndArray();
        writer.WritePropertyName("b");
        writer.WriteStartArray();
        for (int i = 0; i < model.StateCount; i++)
        {
            WriteRow(writer, model.EmissionRow(i));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Reads a model written by <see cref="Write"/>, or any file that follows the format.</summary>
    public static HiddenMarkovModel Read(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The model file is not valid JSON.", e);
        }

        using (document)
        {
            return ReadObject(document.RootElement);
        }
    }

    /// <summary>Reads a model from one JSON object, as written by <see cref="WriteObject"/>.</summary>
    internal static HiddenMarkovModel ReadObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The model file must hold a JSON object.");
        }

        string[] names = [.. root.EnumerateObject().Select(p => p.Name)];
        if (!names.SequenceEqual(PropertyOrder))
        {
            throw new InvalidDataException(
                $"The model file must have exactly these properties, in this order: {string.Join(", ", PropertyOrder)}.");
        }

        if (root.GetProperty("format").ValueKind != JsonValueKind.String || root.GetProperty("format").GetString() != Format)
        {
            throw new InvalidDataException($"The format must be \"{Format}\".");
        }

        if (ReadCount(root, "version") != Version)
        {
            throw new InvalidDataException($"Only version {Version} is supported.");
        }

        int n = ReadCount(root, "states");
        int m = ReadCount(root, "symbols");
        double[] pi = ReadRow(root.GetProperty("pi"), n, "pi");
        double[,] a = ReadMatrix(root.GetProperty("a"), n, n, "a");
        double[,] b = ReadMatrix(root.GetProperty("b"), n, m, "b");
        try
        {
            return new HiddenMarkovModel(pi, a, b);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"The model file holds an invalid model: {e.Message}", e);
        }
    }

    internal static void WriteRow(Utf8JsonWriter writer, ReadOnlySpan<double> row)
    {
        writer.WriteStartArray();
        foreach (double v in row)
        {
            writer.WriteNumberValue(v);
        }

        writer.WriteEndArray();
    }

    internal static int ReadCount(JsonElement root, string name)
    {
        JsonElement e = root.GetProperty(name);
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int value) || value < 1)
        {
            throw new InvalidDataException($"\"{name}\" must be a whole number of at least 1.");
        }

        return value;
    }

    internal static double[] ReadRow(JsonElement e, int length, string name)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != length)
        {
            throw new InvalidDataException($"\"{name}\" must be an array of {length} numbers.");
        }

        var row = new double[length];
        int k = 0;
        foreach (JsonElement item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v) || !double.IsFinite(v))
            {
                throw new InvalidDataException($"\"{name}\" item {k} must be a finite number.");
            }

            row[k++] = v;
        }

        return row;
    }

    private static double[,] ReadMatrix(JsonElement e, int rows, int columns, string name)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != rows)
        {
            throw new InvalidDataException($"\"{name}\" must be an array of {rows} rows.");
        }

        var matrix = new double[rows, columns];
        int r = 0;
        foreach (JsonElement row in e.EnumerateArray())
        {
            double[] values = ReadRow(row, columns, $"{name}[{r}]");
            for (int c = 0; c < columns; c++)
            {
                matrix[r, c] = values[c];
            }

            r++;
        }

        return matrix;
    }
}
