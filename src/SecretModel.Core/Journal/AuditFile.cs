// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.

using System.Globalization;
using System.Text.Json;

namespace SecretModel.Core.Journal;

/// <summary>
/// Writes lines of the ingestion audit log, format <c>secretmodel-audit</c> version 1
/// (docs/formats/audit-v1.md). The server only ever adds lines; it never reads the log back.
/// </summary>
public static class AuditFile
{
    /// <summary>One entry as a line of UTF-8 JSON, without spaces, ending in LF.</summary>
    public static byte[] WriteLine(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("time", entry.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (entry.File is null)
            {
                writer.WriteNull("file");
            }
            else
            {
                writer.WriteString("file", entry.File);
            }

            writer.WriteString("sha256", entry.Sha256);
            writer.WriteString("outcome", OutcomeName(entry.Outcome));
            switch (entry.Outcome)
            {
                case AuditOutcome.Archived:
                case AuditOutcome.Retry:
                    writer.WriteNumber("records", entry.Records);
                    writer.WriteNumber("new", entry.New);
                    writer.WritePropertyName("quarantined");
                    writer.WriteStartArray();
                    foreach (QuarantinedLine line in entry.Quarantined)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(line.LineNumber);
                        writer.WriteStringValue(line.Reason.ToString());
                        writer.WriteEndArray();
                    }

                    writer.WriteEndArray();
                    writer.WriteNumber("cards_trained", entry.CardsTrained);
                    if (entry.Outcome == AuditOutcome.Retry)
                    {
                        writer.WriteNumber("cards_failed", entry.CardsFailed);
                    }

                    break;
                case AuditOutcome.Rejected:
                    writer.WriteNumber("line", entry.RejectedLine);
                    break;
            }

            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static string OutcomeName(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Archived => "archived",
        AuditOutcome.Retry => "retry",
        AuditOutcome.Rejected => "rejected",
        _ => "bad_name",
    };
}
