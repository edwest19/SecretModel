// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using SecretModel.Core.Features;
using SecretModel.Core.Privacy;
using Reason = SecretModel.Core.Journal.JournalQuarantineReason;

namespace SecretModel.Core.Journal;

/// <summary>
/// Reads a journal file in format <c>secretmodel-journal</c> version 1 (docs/formats/journal-v1.md,
/// README §8). Strict: a line that breaks any rule is quarantined with its line number and reason,
/// and the other lines carry on (SM-JRNL-001, SM-JRNL-002). A file in which any line may carry a card
/// number is rejected whole (SM-PRIV-002, SM-JRNL-005). Nothing it returns repeats a line's content.
/// </summary>
public static class JournalFile
{
    /// <summary>The largest <c>seq</c>: 12 digits, one fewer than a possible card number.</summary>
    public const long MaxSeq = 999_999_999_999;

    private const int MaxSeqDigits = 12;

    private static readonly string[] PropertyNames =
        ["seq", "transaction_token", "customer_token", "card_token", "amount_bin", "merchant_category", "hour", "is_online"];

    // .NET 10 accepts a repeated property name by default and keeps the last one (measured).
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    private static readonly LineOutcome RejectsFile = new(true, null, default);

    /// <summary>Reads every line of a journal file.</summary>
    public static JournalReadResult Read(ReadOnlySpan<byte> file)
    {
        var records = new List<JournalRecord>();
        var quarantined = new List<QuarantinedLine>();
        int lineNumber = 0;
        int start = 0;
        while (start < file.Length)
        {
            // A line ends with LF; a CR just before it is removed. The last line needs no LF, and an
            // LF at the very end does not start another line.
            ReadOnlySpan<byte> rest = file[start..];
            int newline = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = newline < 0 ? rest : rest[..newline];
            start += newline < 0 ? rest.Length : newline + 1;
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            lineNumber++;
            LineOutcome outcome = ReadLine(line, lineNumber);
            if (outcome.RejectFile)
            {
                return JournalReadResult.Rejected(lineNumber);
            }

            if (outcome.Record is { } record)
            {
                records.Add(record);
            }
            else
            {
                quarantined.Add(new QuarantinedLine(lineNumber, outcome.Reason));
            }
        }

        return JournalReadResult.Read(records, quarantined);
    }

    private static LineOutcome ReadLine(ReadOnlySpan<byte> line, int lineNumber)
    {
        if (IsBlank(line))
        {
            return Quarantine(Reason.BlankLine);
        }

        // .NET's parser accepts invalid UTF-8 inside a string and fails only when the string is read
        // (measured), so the line is checked first.
        if (!Utf8.IsValid(line))
        {
            return FromRawText(line, Reason.InvalidText);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.ToArray(), Strict);
        }
        catch (JsonException)
        {
            return FromRawText(line, IsJsonApartFromRepeatedNames(line) ? Reason.DuplicateProperty : Reason.NotJson);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            List<string> scanTexts;
            try
            {
                scanTexts = ScanTexts(root);
            }
            catch (InvalidOperationException)
            {
                // A string holds an escape that is not whole Unicode text, such as \ud800 (measured:
                // .NET throws this only when the string is read).
                return FromRawText(line, Reason.InvalidText);
            }

            foreach (string text in scanTexts)
            {
                if (TokenValidator.HasLongDigitRun(text))
                {
                    return RejectsFile;
                }
            }

            return ReadRecord(root, lineNumber);
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (byte b in line)
        {
            if (b is not ((byte)' ' or (byte)'\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJsonApartFromRepeatedNames(ReadOnlySpan<byte> line)
    {
        try
        {
            using JsonDocument lenient = JsonDocument.Parse(line.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A line that is not valid JSON has no values to tell apart, so its whole text is scanned and
    // nothing is exempt, not even a well-formed token. Invalid UTF-8 becomes U+FFFD, which is neither
    // a letter nor a digit and so is ignored, like any other separator. The text is scanned twice: as
    // written, and with JSON's escapes decoded, so 4 or a \n between digits can't hide a run
    // (approved by Edwin West, 2026-11-26).
    private static LineOutcome FromRawText(ReadOnlySpan<byte> line, Reason reason)
    {
        string text = Encoding.UTF8.GetString(line);
        return TokenValidator.HasLongDigitRun(text) || TokenValidator.HasLongDigitRun(DecodeEscapes(text))
            ? RejectsFile
            : Quarantine(reason);
    }

    // Decodes JSON's escapes from left to right, as a JSON reader would, so "\\u0034" is a backslash
    // and then "u0034", not the digit 4. Anything after a backslash that is not an escape is kept.
    private static string DecodeEscapes(string text)
    {
        var decoded = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 == text.Length)
            {
                decoded.Append(c);
                continue;
            }

            char next = text[i + 1];
            char? simple = next switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => null,
            };
            if (simple is { } escaped)
            {
                decoded.Append(escaped);
                i++;
            }
            else if (next == 'u' && i + 5 < text.Length
                && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort unit))
            {
                decoded.Append((char)unit);
                i += 5;
            }
            else
            {
                decoded.Append(c);
            }
        }

        return decoded.ToString();
    }

    // The texts scanned for a long run of digits in a line that is valid JSON. An object's property
    // names and values are scanned separately; any other value is scanned whole.
    private static List<string> ScanTexts(JsonElement root)
    {
        var texts = new List<string>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                var name = new StringBuilder();
                AppendString(property.Name, name);
                texts.Add(name.ToString());

                var value = new StringBuilder();
                AppendValue(property.Value, value);
                texts.Add(value.ToString());
            }
        }
        else
        {
            var value = new StringBuilder();
            AppendValue(root, value);
            texts.Add(value.ToString());
        }

        return texts;
    }

    // An array or object counts as one value: everything in it, in order, joined by a space, which
    // the scan ignores. A number counts as its text as written; true, false and null as themselves.
    private static void AppendValue(JsonElement element, StringBuilder text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AppendString(element.GetString()!, text);
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    text.Append(' ');
                    AppendValue(item, text);
                }

                break;

            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    text.Append(' ');
                    AppendString(property.Name, text);
                    text.Append(' ');
                    AppendValue(property.Value, text);
                }

                break;

            default:
                text.Append(element.GetRawText());
                break;
        }
    }

    // A string counts as its decoded text, so an escape such as 4 counts as the digit it stands
    // for. A well-formed token (SM-PRIV-001, any kind) counts as nothing: tokens are exempt.
    private static void AppendString(string value, StringBuilder text)
    {
        if (!TokenValidator.IsValid(value))
        {
            text.Append(value);
        }
    }

    private static LineOutcome ReadRecord(JsonElement root, int lineNumber)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Quarantine(Reason.NotAnObject);
        }

        // The strict parse has already refused repeated names, so eight known names means all eight.
        int known = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (Array.IndexOf(PropertyNames, property.Name) < 0)
            {
                return Quarantine(Reason.UnknownProperty);
            }

            known++;
        }

        if (known != PropertyNames.Length)
        {
            return Quarantine(Reason.MissingProperty);
        }

        if (!TryReadWholeNumber(root.GetProperty("seq"), MaxSeq, out long seq))
        {
            return Quarantine(Reason.InvalidSeq);
        }

        if (!TryReadToken(root.GetProperty("transaction_token"), "tok_txn_", out string transactionToken))
        {
            return Quarantine(Reason.InvalidTransactionToken);
        }

        if (!TryReadToken(root.GetProperty("customer_token"), "tok_cus_", out string customerToken))
        {
            return Quarantine(Reason.InvalidCustomerToken);
        }

        if (!TryReadToken(root.GetProperty("card_token"), "tok_card_", out string cardToken))
        {
            return Quarantine(Reason.InvalidCardToken);
        }

        if (!TryReadWholeNumber(root.GetProperty("amount_bin"), Quantizer.AmountBinCount - 1, out long amountBin))
        {
            return Quarantine(Reason.InvalidAmountBin);
        }

        // Exactly one of the eight names. Not lowercased and not read as "other": the archive would
        // otherwise keep free text (README SM-PRIV-003).
        JsonElement category = root.GetProperty("merchant_category");
        string? categoryName = category.ValueKind == JsonValueKind.String ? category.GetString() : null;
        if (categoryName is null || !Quantizer.CategoryNames.Contains(categoryName, StringComparer.Ordinal))
        {
            return Quarantine(Reason.InvalidMerchantCategory);
        }

        if (!TryReadWholeNumber(root.GetProperty("hour"), 23, out long hour))
        {
            return Quarantine(Reason.InvalidHour);
        }

        JsonElement online = root.GetProperty("is_online");
        if (online.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Quarantine(Reason.InvalidIsOnline);
        }

        var record = new JournalRecord(
            lineNumber,
            seq,
            transactionToken,
            customerToken,
            cardToken,
            (int)amountBin,
            categoryName,
            (int)hour,
            online.ValueKind == JsonValueKind.True);
        return new LineOutcome(false, record, default);
    }

    // Plain decimal digits only: no sign, fraction or exponent, so 14.0, 1e1 and -0 are refused.
    // JSON itself refuses leading zeros.
    private static bool TryReadWholeNumber(JsonElement element, long max, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        string text = element.GetRawText();
        if (text.Length > MaxSeqDigits)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        value = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return value <= max;
    }

    private static bool TryReadToken(JsonElement element, string prefix, out string token)
    {
        token = "";
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string value = element.GetString()!;
        if (!TokenValidator.IsValid(value) || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        token = value;
        return true;
    }

    private static LineOutcome Quarantine(Reason reason) => new(false, null, reason);

    private readonly record struct LineOutcome(bool RejectFile, JournalRecord? Record, Reason Reason);
}
