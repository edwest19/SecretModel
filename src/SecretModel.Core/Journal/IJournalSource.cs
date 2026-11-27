// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.

namespace SecretModel.Core.Journal;

/// <summary>
/// A journal file waiting to be ingested: the client folder it is in and its name, both exactly as
/// the source found them. <see cref="JournalIngestor"/> checks both names before trusting them.
/// </summary>
public sealed record JournalFileRef(string ClientId, string Name);

/// <summary>Where in its client's folder a handled journal file goes (README §8.2).</summary>
public enum JournalFolder
{
    /// <summary><c>archive/</c>: the file was ingested (README SM-JRNL-006).</summary>
    Archive,

    /// <summary><c>quarantine/</c>: the file was rejected or had a bad name. It is never read again.</summary>
    Quarantine,
}

/// <summary>
/// Where journal files arrive (README §8): a local folder in Home mode. Each client has a folder of
/// its own, named by its client id; the client of a file comes from that folder, never from the file
/// (README SM-AUTH-003). Holds no state between calls.
/// </summary>
public interface IJournalSource
{
    /// <summary>
    /// Every file directly inside a client folder whose name ends in <see cref="JournalNames.Extension"/>,
    /// ordered by folder name and then file name (ordinal). Nothing in <c>archive/</c> or
    /// <c>quarantine/</c> is listed.
    /// </summary>
    Task<IReadOnlyList<JournalFileRef>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The file's bytes. Pass only a file from <see cref="ListAsync"/>.</summary>
    Task<byte[]> ReadAsync(JournalFileRef file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the file, unchanged, into its client's <paramref name="folder"/>, named
    /// <paramref name="targetName"/>; when that name is taken, the lowest free name with a number
    /// before the extension (<c>day1.1.jsonl</c>, <c>day1.2.jsonl</c>, …). Returns the name used.
    /// </summary>
    Task<string> MoveAsync(JournalFileRef file, JournalFolder folder, string targetName, CancellationToken cancellationToken = default);
}
