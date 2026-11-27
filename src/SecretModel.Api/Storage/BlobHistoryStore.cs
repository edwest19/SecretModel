// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 8c-1). AI-generated.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;

namespace SecretModel.Api.Storage;

/// <summary>
/// The Azure-mode symbol history store (README §9.2): one block blob per card,
/// <c>{client hash}/{customer token}/{card token}.history.json</c>, next to the card's profile in the
/// same container, which the deployment creates. Format <c>secretmodel-history</c> version 1
/// (docs/formats/history-v1.md). The version is the blob's ETag and every write is conditional, as
/// for profiles (SM-STOR-002, SM-STOR-004). Nothing is cached in memory (SM-STOR-003).
/// </summary>
public sealed class BlobHistoryStore : IHistoryStore
{
    public BlobHistoryStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        Container = container;
    }

    public BlobContainerClient Container { get; }

    /// <summary>The history's blob name.</summary>
    public static string BlobNameFor(ProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"{key.ClientHash}/{key.CustomerToken}/{key.CardToken}.history.json";
    }

    public async Task<StoredHistory?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default)
    {
        BlobDownloadResult result;
        try
        {
            result = (await Container.GetBlobClient(BlobNameFor(key)).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.BlobNotFound))
        {
            // Only a missing blob means "no history". A missing container, or anything else, is an error.
            return null;
        }

        return new StoredHistory(CardHistoryFile.Read(result.Content.ToMemory().Span), result.Details.ETag.ToString());
    }

    public async Task<string> PutAsync(
        ProfileKey key, CardHistory history, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var options = new BlobUploadOptions
        {
            Conditions = expectedVersion is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(expectedVersion) },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        };

        try
        {
            var response = await Container.GetBlobClient(BlobNameFor(key))
                .UploadAsync(BinaryData.FromBytes(CardHistoryFile.Write(history)), options, cancellationToken);
            return response.Value.ETag.ToString();
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.BlobAlreadyExists))
        {
            throw new ConcurrencyConflictException("A history is already stored for this card.");
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.ConditionNotMet))
        {
            // Blob Storage answers the same way whether the history was replaced or deleted.
            throw new ConcurrencyConflictException("The stored history was changed or removed by another writer.");
        }
    }
}
