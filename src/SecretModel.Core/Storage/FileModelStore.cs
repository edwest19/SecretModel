// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated. On 2026-11-26
// its file handling moved, unchanged, to VersionedFile, which the history store shares.

using SecretModel.Core.Scoring;

namespace SecretModel.Core.Storage;

/// <summary>
/// The Home-mode profile store (README §9): one file per card under a root folder, at
/// <see cref="ProfileKey.RelativePath"/>. A profile's version is the SHA-256 of its file. A write
/// takes an exclusive lock file next to the profile, checks the version, writes a temporary file
/// and renames it over the profile, so a reader sees the old file or the new one, never a mix
/// (<see cref="VersionedFile"/>). Nothing is cached in memory (SM-STOR-003).
/// </summary>
public sealed class FileModelStore : IModelStore
{
    public FileModelStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>The profile's file path on this machine.</summary>
    public string PathFor(ProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(Root, key.ClientHash, key.CustomerToken, key.CardToken + ".profile.json");
    }

    public async Task<StoredProfile?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await VersionedFile.ReadIfExistsAsync(PathFor(key), cancellationToken);
        return bytes is null ? null : new StoredProfile(CardProfileFile.Read(bytes), VersionedFile.VersionOf(bytes));
    }

    public async Task<string> PutAsync(
        ProfileKey key, CardProfile profile, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string path = PathFor(key);
        return await VersionedFile.WriteAsync(path, CardProfileFile.Write(profile), expectedVersion, "profile", cancellationToken);
    }
}
