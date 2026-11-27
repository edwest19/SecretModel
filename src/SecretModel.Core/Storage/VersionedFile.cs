// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26. AI-generated. Moved here
// unchanged from FileModelStore (2026-11-25) so the profile and history stores share one copy.

using System.Security.Cryptography;

namespace SecretModel.Core.Storage;

/// <summary>
/// The Home-mode file handling shared by <see cref="FileModelStore"/> and
/// <see cref="FileHistoryStore"/> (README §9.1): a file's version is the SHA-256 of its bytes; a write
/// takes an exclusive lock file next to the file, checks the version, writes a temporary file and
/// renames it over the file, so a reader sees the old file or the new one, never a mix.
/// </summary>
internal static class VersionedFile
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The version of a file: the SHA-256 of its bytes, as lowercase hex.</summary>
    internal static string VersionOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Replaces the file at <paramref name="path"/> with <paramref name="bytes"/>, only if its version
    /// is still <paramref name="expectedVersion"/>, or, when that is null, only if there is no file.
    /// Returns the new version; otherwise throws <see cref="ConcurrencyConflictException"/>, whose
    /// message names the <paramref name="noun"/> ("profile", "history"), and changes nothing.
    /// </summary>
    internal static async Task<string> WriteAsync(
        string path, byte[] bytes, string? expectedVersion, string noun, CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);

        using (await AcquireLockAsync(path + ".lock", cancellationToken))
        {
            byte[]? current = await ReadIfExistsAsync(path, cancellationToken);
            string? currentVersion = current is null ? null : VersionOf(current);
            if (currentVersion != expectedVersion)
            {
                throw new ConcurrencyConflictException(
                    expectedVersion is null ? $"A {noun} is already stored for this card."
                    : currentVersion is null ? $"No {noun} is stored for this card any more."
                    : $"The stored {noun} was changed by another writer.");
            }

            // A failed or interrupted write can leave this file behind; it is never read.
            string temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            await ReplaceAsync(temporary, path, cancellationToken);
        }

        return VersionOf(bytes);
    }

    // FileShare.Delete lets a writer rename a new file over this one while it is being read (Windows).
    internal static async Task<byte[]?> ReadIfExistsAsync(string path, CancellationToken cancellationToken)
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
