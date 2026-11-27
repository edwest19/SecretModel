// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.

using SecretModel.Core.Journal;

namespace SecretModel.Core.Storage;

/// <summary>
/// The Home-mode audit log (README §8.2): <c>{root}/{client hash}/audit.jsonl</c> under the same root
/// as the profiles and histories, so the client id never appears in the path (README SM-STOR-001).
/// Each entry is one line in format <c>secretmodel-audit</c> version 1 (docs/formats/audit-v1.md).
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    public const string FileName = "audit.jsonl";

    public FileAuditLog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>The client's audit log path on this machine.</summary>
    public string PathFor(string clientId) => Path.Combine(Root, ProfileKey.ClientHashOf(clientId), FileName);

    public async Task AppendAsync(string clientId, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string path = PathFor(clientId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllBytesAsync(path, AuditFile.WriteLine(entry), cancellationToken);
    }
}
