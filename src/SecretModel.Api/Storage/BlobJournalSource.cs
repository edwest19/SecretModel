// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by
// Edwin West on 2026-11-27 (Task 8c-2). AI-generated.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Core.Journal;

namespace SecretModel.Api.Storage;

/// <summary>
/// The Azure-mode journal source (README §8.3): the <c>journal</c> container behind the storage
/// account's SFTP endpoint, created by the deployment. Each folder at the top of the container is a
/// client's, <c>{client id}/</c>, and the server adds <c>archive/</c> and <c>quarantine/</c> inside it.
/// Every listed file carries its ETag, and reading, copying and deleting all hold to that version.
/// Blob Storage has no rename, so a move writes the copy only if its name is free and then deletes
/// the original only if it is unchanged. Nothing is cached between calls.
/// </summary>
public sealed class BlobJournalSource : IJournalSource
{
    // With a hierarchical namespace, directories are listed as zero-length blobs with this metadata.
    internal const string DirectoryMetadata = "hdi_isfolder";

    public BlobJournalSource(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        Container = container;
    }

    public BlobContainerClient Container { get; }

    public async Task<IReadOnlyList<JournalFileRef>> ListAsync(CancellationToken cancellationToken = default)
    {
        var clients = new List<string>();
        await foreach (BlobHierarchyItem item in Container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", null, cancellationToken))
        {
            if (item.IsPrefix)
            {
                clients.Add(item.Prefix[..^1]);
            }
        }

        var files = new List<JournalFileRef>();
        foreach (string client in clients.Order(StringComparer.Ordinal))
        {
            var found = new List<JournalFileRef>();
            await foreach (BlobHierarchyItem item in Container.GetBlobsByHierarchyAsync(
                BlobTraits.Metadata, BlobStates.None, "/", client + "/", cancellationToken))
            {
                if (item.IsBlob && !IsDirectory(item.Blob) && item.Blob.Name.EndsWith(JournalNames.Extension, StringComparison.Ordinal))
                {
                    // A listing gives the ETag without quotes and a response header with them (measured
                    // on Azurite); the header form is used, as the other stores report it.
                    found.Add(new JournalFileRef(client, item.Blob.Name[(client.Length + 1)..], item.Blob.Properties.ETag?.ToString("H")));
                }
            }

            // A hierarchical namespace lists in depth-first order, not by name.
            files.AddRange(found.OrderBy(f => f.Name, StringComparer.Ordinal));
        }

        return files;
    }

    public async Task<byte[]> ReadAsync(JournalFileRef file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrEmpty(file.Version);
        try
        {
            var options = new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = new ETag(file.Version) } };
            return (await Container.GetBlobClient(BlobName(file)).DownloadContentAsync(options, cancellationToken)).Value.Content.ToArray();
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.ConditionNotMet) || BlobErrors.Is(e, BlobErrorCode.BlobNotFound))
        {
            // The message names no file: a name that breaks the rules may hold a card number.
            throw new IOException("A journal file changed or was removed after it was listed; it is read again on the next run.");
        }
    }

    public async Task<string> MoveAsync(JournalFileRef file, JournalFolder folder, string targetName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        byte[] bytes = await ReadAsync(file, cancellationToken);
        string target = $"{file.ClientId}/{(folder == JournalFolder.Archive ? FolderJournalSource.ArchiveFolderName : FolderJournalSource.QuarantineFolderName)}/";
        string stem = Path.GetFileNameWithoutExtension(targetName);
        string extension = Path.GetExtension(targetName);

        string? used = null;
        for (int n = 0; used is null; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = n == 0 ? targetName : $"{stem}.{n}{extension}";
            try
            {
                var options = new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } };
                await Container.GetBlobClient(target + name).UploadAsync(BinaryData.FromBytes(bytes), options, cancellationToken);
                used = name;
            }
            catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.BlobAlreadyExists))
            {
                // Taken: try the next number. Nothing is overwritten.
            }
        }

        try
        {
            await Container.GetBlobClient(BlobName(file)).DeleteAsync(
                DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = new ETag(file.Version!) }, cancellationToken);
        }
        catch (RequestFailedException e) when (BlobErrors.Is(e, BlobErrorCode.ConditionNotMet) || BlobErrors.Is(e, BlobErrorCode.BlobNotFound))
        {
            // The client replaced or removed the file after it was read. The copy of what was read
            // stays, and a replacement is read on the next run.
        }

        return used;
    }

    private static string BlobName(JournalFileRef file) => $"{file.ClientId}/{file.Name}";

    private static bool IsDirectory(BlobItem blob) =>
        blob.Metadata is not null
        && blob.Metadata.TryGetValue(DirectoryMetadata, out string? value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
