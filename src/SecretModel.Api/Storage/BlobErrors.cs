// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27. AI-generated.

using Azure;
using Azure.Storage.Blobs.Models;

namespace SecretModel.Api.Storage;

/// <summary>Which Blob Storage error a failed request carries.</summary>
internal static class BlobErrors
{
    public static bool Is(RequestFailedException e, BlobErrorCode code) =>
        string.Equals(e.ErrorCode, code.ToString(), StringComparison.Ordinal);
}
