// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.

using SecretModel.Core.Journal;

namespace SecretModel.Core.Storage;

/// <summary>
/// Where ingestion records each journal file it handled (README SM-JRNL-008): one log per client,
/// kept with the client's models, only ever added to.
/// </summary>
public interface IAuditLog
{
    /// <summary>Adds the entry at the end of the client's audit log.</summary>
    Task AppendAsync(string clientId, AuditEntry entry, CancellationToken cancellationToken = default);
}
