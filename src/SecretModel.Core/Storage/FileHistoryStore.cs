// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated.

using SecretModel.Core.Scoring;

namespace SecretModel.Core.Storage;

/// <summary>
/// The Home-mode history store (README §9.3): one file per card under a root folder, next to its
/// profile, at <c>{client hash}/{customer token}/{card token}.history.json</c>. Versions, locking and
/// writes work exactly as for profiles (<see cref="VersionedFile"/>), with a lock file of its own.
/// Nothing is cached in memory (SM-STOR-003).
/// </summary>
public sealed class FileHistoryStore : IHistoryStore
{
    public FileHistoryStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>The history's file path on this machine.</summary>
    public string PathFor(ProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(Root, key.ClientHash, key.CustomerToken, key.CardToken + ".history.json");
    }

    public async Task<StoredHistory?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await VersionedFile.ReadIfExistsAsync(PathFor(key), cancellationToken);
        return bytes is null ? null : new StoredHistory(CardHistoryFile.Read(bytes), VersionedFile.VersionOf(bytes));
    }

    public async Task<string> PutAsync(
        ProfileKey key, CardHistory history, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        string path = PathFor(key);
        return await VersionedFile.WriteAsync(path, CardHistoryFile.Write(history), expectedVersion, "history", cancellationToken);
    }
}
