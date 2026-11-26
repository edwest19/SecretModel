// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using SecretModel.Core.Scoring;

namespace SecretModel.Core.Storage;

/// <summary>A stored profile and the version it was read at.</summary>
public sealed record StoredProfile(CardProfile Profile, string Version);

/// <summary>
/// Thrown when a write's expected version no longer matches what is stored: another writer got there
/// first (README SM-STOR-002). The losing writer must read again and retry, or give up loudly.
/// </summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>
/// Where card profiles live (README §9): a local folder in Home mode, Blob Storage in Azure mode.
/// Holds no state of its own between calls (SM-STOR-003).
/// </summary>
public interface IModelStore
{
    /// <summary>The stored profile and its version, or null when there is none.</summary>
    Task<StoredProfile?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the profile only if the stored version is still <paramref name="expectedVersion"/>, or,
    /// when that is null, only if nothing is stored yet. Returns the new version; throws
    /// <see cref="ConcurrencyConflictException"/> otherwise and leaves what is stored unchanged.
    /// </summary>
    Task<string> PutAsync(ProfileKey key, CardProfile profile, string? expectedVersion, CancellationToken cancellationToken = default);
}
