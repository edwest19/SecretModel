// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.

using SecretModel.Core.Scoring;

namespace SecretModel.Core.Storage;

/// <summary>A stored history and the version it was read at.</summary>
public sealed record StoredHistory(CardHistory History, string Version);

/// <summary>
/// Where each card's symbol history lives (README §9.3), keyed like its profile: a local folder in
/// Home mode. Holds no state of its own between calls (SM-STOR-003).
/// </summary>
public interface IHistoryStore
{
    /// <summary>The stored history and its version, or null when there is none.</summary>
    Task<StoredHistory?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the history only if the stored version is still <paramref name="expectedVersion"/>, or,
    /// when that is null, only if nothing is stored yet. Returns the new version; throws
    /// <see cref="ConcurrencyConflictException"/> otherwise and leaves what is stored unchanged.
    /// </summary>
    Task<string> PutAsync(ProfileKey key, CardHistory history, string? expectedVersion, CancellationToken cancellationToken = default);
}
