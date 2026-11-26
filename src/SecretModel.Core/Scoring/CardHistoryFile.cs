// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.

using System.Text.Json;

namespace SecretModel.Core.Scoring;

/// <summary>
/// Reads and writes the card history file, format "secretmodel-history" version 1
/// (docs/formats/history-v1.md). The reader is strict and rejects anything else with
/// <see cref="InvalidDataException"/>.
/// </summary>
public static class CardHistoryFile
{
    public const string Format = "secretmodel-history";
    public const int Version = 1;

    private static readonly string[] PropertyOrder = ["format", "version", "seq", "symbols"];

    /// <summary>The history as UTF-8 JSON: one line, no byte-order mark, no trailing newline.</summary>
    public static byte[] Write(CardHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WritePropertyName("seq");
            writer.WriteStartArray();
            foreach (long s in history.Seq)
            {
                writer.WriteNumberValue(s);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("symbols");
            writer.WriteStartArray();
            foreach (int s in history.Symbols)
            {
                writer.WriteNumberValue(s);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Reads a history written by <see cref="Write"/>, or any file that follows the format.</summary>
    public static CardHistory Read(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The history file is not valid JSON.", e);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The history file must hold a JSON object.");
            }

            // A repeated property makes the list longer, so it is refused here too.
            string[] names = [.. root.EnumerateObject().Select(p => p.Name)];
            if (!names.SequenceEqual(PropertyOrder))
            {
                throw new InvalidDataException(
                    $"The history file must have exactly these properties, in this order: {string.Join(", ", PropertyOrder)}.");
            }

            JsonElement format = root.GetProperty("format");
            if (format.ValueKind != JsonValueKind.String || format.GetString() != Format)
            {
                throw new InvalidDataException($"The format must be \"{Format}\".");
            }

            if (!TryReadWholeNumber(root.GetProperty("version"), out long version) || version != Version)
            {
                throw new InvalidDataException($"Only version {Version} is supported.");
            }

            long[] seq = ReadWholeNumbers(root.GetProperty("seq"), "seq");
            long[] symbols = ReadWholeNumbers(root.GetProperty("symbols"), "symbols");
            if (symbols.Any(s => s > int.MaxValue))
            {
                throw new InvalidDataException("\"symbols\" holds a number that is not a symbol.");
            }

            try
            {
                return new CardHistory(seq, [.. symbols.Select(s => (int)s)]);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"The history file holds an invalid history: {e.Message}", e);
            }
        }
    }

    private static long[] ReadWholeNumbers(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"\"{name}\" must be an array of whole numbers.");
        }

        var values = new long[e.GetArrayLength()];
        int k = 0;
        foreach (JsonElement item in e.EnumerateArray())
        {
            if (!TryReadWholeNumber(item, out values[k]))
            {
                throw new InvalidDataException($"\"{name}\" item {k} must be a whole number written as plain digits.");
            }

            k++;
        }

        return values;
    }

    // Plain decimal digits only, as the writer writes them: no sign, fraction or exponent. At most 12
    // digits, the most a seq can have (JSON itself refuses leading zeros).
    private static bool TryReadWholeNumber(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        string text = element.GetRawText();
        if (text.Length > 12 || text.Any(c => c is < '0' or > '9'))
        {
            return false;
        }

        value = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}
