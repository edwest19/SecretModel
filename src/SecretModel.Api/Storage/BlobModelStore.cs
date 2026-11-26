// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;

namespace SecretModel.Api.Storage;

/// <summary>
/// The Azure-mode profile store (README §9.2): one block blob per card, named
/// <see cref="ProfileKey.RelativePath"/>, in a container that the deployment creates. A profile's
/// version is the blob's ETag, and every write is conditional (If-None-Match: * to create, If-Match
/// to replace), so Blob Storage itself refuses a write that lost a race, however many server
/// instances there are (SM-STOR-002, SM-STOR-004). Nothing is cached in memory (SM-STOR-003).
/// </summary>
public sealed class BlobModelStore : IModelStore
{
    public BlobModelStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        Container = container;
    }

    public BlobContainerClient Container { get; }

    public async Task<StoredProfile?> GetAsync(ProfileKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        BlobDownloadResult result;
        try
        {
            result = (await Container.GetBlobClient(key.RelativePath).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.BlobNotFound))
        {
            // Only a missing blob means "no profile". A missing container, or anything else, is an error.
            return null;
        }

        return new StoredProfile(CardProfileFile.Read(result.Content.ToMemory().Span), result.Details.ETag.ToString());
    }

    public async Task<string> PutAsync(
        ProfileKey key, CardProfile profile, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(profile);
        var options = new BlobUploadOptions
        {
            Conditions = expectedVersion is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(expectedVersion) },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        };

        try
        {
            var response = await Container.GetBlobClient(key.RelativePath)
                .UploadAsync(BinaryData.FromBytes(CardProfileFile.Write(profile)), options, cancellationToken);
            return response.Value.ETag.ToString();
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.BlobAlreadyExists))
        {
            throw new ConcurrencyConflictException("A profile is already stored for this card.");
        }
        catch (RequestFailedException e) when (Is(e, BlobErrorCode.ConditionNotMet))
        {
            // Blob Storage answers the same way whether the profile was replaced or deleted.
            throw new ConcurrencyConflictException("The stored profile was changed or removed by another writer.");
        }
    }

    private static bool Is(RequestFailedException e, BlobErrorCode code) =>
        string.Equals(e.ErrorCode, code.ToString(), StringComparison.Ordinal);
}
