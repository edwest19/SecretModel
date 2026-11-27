// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the Home-mode folders
// approved by Edwin West on 2026-11-26 (design C). AI-generated.

namespace SecretModel.Core.Journal;

/// <summary>
/// The Home-mode journal source (README §8.2): a local folder holding one folder per client,
/// <c>{journal root}/{client id}/</c>, into which the client places its files by any means. The
/// server adds <c>archive/</c> and <c>quarantine/</c> inside each client folder when it first needs
/// them. Nothing is cached between calls.
/// </summary>
public sealed class FolderJournalSource : IJournalSource
{
    public const string ArchiveFolderName = "archive";
    public const string QuarantineFolderName = "quarantine";

    public FolderJournalSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public Task<IReadOnlyList<JournalFileRef>> ListAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<JournalFileRef>();
        foreach (string client in Directory.GetDirectories(Root).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> names = Directory.GetFiles(Path.Combine(Root, client))
                .Select(f => Path.GetFileName(f))
                .Where(n => n.EndsWith(JournalNames.Extension, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);
            files.AddRange(names.Select(n => new JournalFileRef(client, n)));
        }

        return Task.FromResult<IReadOnlyList<JournalFileRef>>(files);
    }

    public Task<byte[]> ReadAsync(JournalFileRef file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return File.ReadAllBytesAsync(Path.Combine(Root, file.ClientId, file.Name), cancellationToken);
    }

    public Task<string> MoveAsync(JournalFileRef file, JournalFolder folder, string targetName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        string source = Path.Combine(Root, file.ClientId, file.Name);
        string target = Path.Combine(Root, file.ClientId, folder == JournalFolder.Archive ? ArchiveFolderName : QuarantineFolderName);
        Directory.CreateDirectory(target);

        string stem = Path.GetFileNameWithoutExtension(targetName);
        string extension = Path.GetExtension(targetName);
        for (int n = 0; ; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = n == 0 ? targetName : $"{stem}.{n}{extension}";
            string destination = Path.Combine(target, name);
            if (!File.Exists(destination))
            {
                // Never overwrites: if another mover takes the name first, this throws (one run at a time, README §8.2).
                File.Move(source, destination, overwrite: false);
                return Task.FromResult(name);
            }
        }
    }
}
