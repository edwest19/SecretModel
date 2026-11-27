// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 8c-1). AI-generated.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Core.Journal;
using SecretModel.Core.Storage;

namespace SecretModel.Api.Storage;

/// <summary>
/// The Azure-mode audit log (README §9.2): one block blob per client, <c>{client hash}/audit.jsonl</c>,
/// in the model container, holding exactly the bytes the Home-mode log would (format
/// <c>secretmodel-audit</c> version 1, docs/formats/audit-v1.md). Adding a line reads the blob, adds
/// the line, and writes it back only if the blob is unchanged since the read (its ETag), so an append
/// that lost a race fails loudly and never drops another writer's line.
/// </summary>
public sealed class BlobAuditLog : IAuditLog
{
    public const string ContentType = "application/x-ndjson";

    public BlobAuditLog(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        Container = container;
    }

    public BlobContainerClient Container { get; }

    /// <summary>The client's audit log blob name.</summary>
    public static string BlobNameFor(string clientId) => $"{ProfileKey.ClientHashOf(clientId)}/{FileAuditLog.FileName}";

    public async Task AppendAsync(string clientId, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        BlobClient blob = Container.GetBlobClient(BlobNameFor(clientId));
        byte[] line = AuditFile.WriteLine(entry);

        byte[] content;
        BlobRequestConditions conditions;
        try
        {
            BlobDownloadResult existing = (await blob.DownloadContentAsync(cancellationToken)).Value;
            content = [.. existing.Content.ToArray(), .. line];
            conditions = new BlobRequestConditions { IfMatch = existing.Details.ETag };
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.BlobNotFound))
        {
            // The client's first entry. A missing container, or anything else, is an error.
            content = line;
            conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        var options = new BlobUploadOptions { Conditions = conditions, HttpHeaders = new BlobHttpHeaders { ContentType = ContentType } };
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(content), options, cancellationToken);
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.BlobAlreadyExists))
        {
            throw new ConcurrencyConflictException("Another writer started this client's audit log at the same time.");
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.ConditionNotMet))
        {
            throw new ConcurrencyConflictException("This client's audit log was changed or removed by another writer.");
        }
    }
}
