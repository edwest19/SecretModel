// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using System.Security.Cryptography;
using SecretModel.Core.Scoring;

namespace SecretModel.Core.Storage;

/// <summary>
/// The Home-mode profile store (README §9): one file per card under a root folder, at
/// <see cref="ProfileKey.RelativePath"/>. A profile's version is the SHA-256 of its file. A write
/// takes an exclusive lock file next to the profile, checks the version, writes a temporary file
/// and renames it over the profile, so a reader sees the old file or the new one, never a mix.
/// Nothing is cached in memory (SM-STOR-003).
/// </summary>
public sealed class FileModelStore : IModelStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

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
        byte[]? bytes = await ReadIfExistsAsync(PathFor(key), cancellationToken);
        return bytes is null ? null : new StoredProfile(CardProfileFile.Read(bytes), VersionOf(bytes));
    }

    public async Task<string> PutAsync(
        ProfileKey key, CardProfile profile, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string path = PathFor(key);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        byte[] bytes = CardProfileFile.Write(profile);

        using (await AcquireLockAsync(path + ".lock", cancellationToken))
        {
            byte[]? current = await ReadIfExistsAsync(path, cancellationToken);
            string? currentVersion = current is null ? null : VersionOf(current);
            if (currentVersion != expectedVersion)
            {
                throw new ConcurrencyConflictException(
                    expectedVersion is null ? "A profile is already stored for this card."
                    : currentVersion is null ? "No profile is stored for this card any more."
                    : "The stored profile was changed by another writer.");
            }

            // A failed or interrupted write can leave this file behind; it is never read.
            string temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            await ReplaceAsync(temporary, path, cancellationToken);
        }

        return VersionOf(bytes);
    }

    // Windows refuses to rename over a file that a reader has open, even one opened with
    // FileShare.Delete (measured on windows-latest: UnauthorizedAccessException). A reader holds the
    // file only while it copies the bytes, so try again, still holding the lock, until the timeout.
    private static async Task ReplaceAsync(string temporary, string path, CancellationToken cancellationToken)
    {
        DateTime giveUp = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private static string VersionOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // FileShare.Delete lets a writer rename a new file over this one while it is being read (Windows).
    private static async Task<byte[]?> ReadIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    // An exclusive handle on a lock file: other writers, in this process or another, wait for it.
    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        DateTime giveUp = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < giveUp)
            {
                await Task.Delay(5, cancellationToken);
            }
        }
    }
}
