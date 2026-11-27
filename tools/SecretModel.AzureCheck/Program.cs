// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, for Task 8c step 6 (approved by
// Edwin West on 2026-11-27). AI-generated.
//
// Checks the Azure-mode stores and journal source (README §8.3, §9.2) against a real storage account,
// for what Azurite can't show: a hierarchical namespace, and files that arrived through the SFTP
// endpoint. Run on Edwin's PC from the repository root, with the storage connection string in the
// environment variable SECRETMODEL_AZURE_STORAGE for that window only (docs/azure/storage-setup.md):
//
//   dotnet run -c Release --project tools/SecretModel.AzureCheck -- check            automatic checks; removes what it made
//   dotnet run -c Release --project tools/SecretModel.AzureCheck -- list             what the journal container holds
//   dotnet run -c Release --project tools/SecretModel.AzureCheck -- ingest           one ingestion run over the journal container
//   dotnet run -c Release --project tools/SecretModel.AzureCheck -- sample <file>    writes a small made-up journal to upload by SFTP
//
// It never creates a container, never prints the connection string or a Blob error's message (which
// holds addresses), and never prints a name that breaks the README §8.2 name rules. The exit code is
// the number of failed checks, or 2 to 4 for a problem that stopped it.

using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecretModel.Api.Storage;
using SecretModel.Core.Features;
using SecretModel.Core.Journal;
using SecretModel.Core.Scoring;
using SecretModel.Core.Storage;

const string Variable = "SECRETMODEL_AZURE_STORAGE";
string mode = args.Length > 0 ? args[0] : "";

if (mode == "sample")
{
    if (args.Length != 2)
    {
        return Usage();
    }

    File.WriteAllText(args[1], Check.SampleJournal(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"Wrote a made-up journal of 15 transactions for two cards to {args[1]}.");
    return 0;
}

if (mode is not ("check" or "list" or "ingest"))
{
    return Usage();
}

string? connection = Environment.GetEnvironmentVariable(Variable);
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine($"Set {Variable} to the storage account's connection string first (docs/azure/storage-setup.md).");
    return 2;
}

var service = new BlobServiceClient(connection);
var check = new Check(service.GetBlobContainerClient("journal"), service.GetBlobContainerClient("models"));
try
{
    return mode switch
    {
        "check" => await check.RunAsync(),
        "list" => await check.ListAsync(),
        _ => await check.IngestAsync(),
    };
}
catch (RequestFailedException e)
{
    Console.Error.WriteLine($"Blob Storage refused a request: HTTP {e.Status}, {e.ErrorCode ?? "no error code"}.");
    return 3;
}
catch (IOException e)
{
    // The journal source's own messages name no file.
    Console.Error.WriteLine($"Stopped: {e.Message}");
    return 4;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: check | list | ingest | sample <file>   (docs/azure/storage-setup.md)");
    return 2;
}

internal sealed class Check(BlobContainerClient journal, BlobContainerClient models)
{
    // With a hierarchical namespace, directories are listed as zero-length blobs with this metadata.
    private const string DirectoryMetadata = "hdi_isfolder";
    private const string Customer = "tok_cus_aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CardA = "tok_card_cccccccccccccccccccccccc";
    private const string CardB = "tok_card_dddddddddddddddddddddddd";

    private int failures;

    // ---- check ------------------------------------------------------------------------------------

    public async Task<int> RunAsync()
    {
        // Letters only: a client id may not hold a run of 13 digits.
        string client = "azurecheck-" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))));
        string ingestClient = client + "-ingest";
        Console.WriteLine($"Test clients: {client} and {ingestClient}. Everything they make is removed at the end.");
        try
        {
            if (!Report("C1", "the containers journal and models exist", (await journal.ExistsAsync()).Value && (await models.ExistsAsync()).Value))
            {
                return failures;
            }

            await CheckProfilesAsync(client);
            await CheckHistoriesAsync(client);
            await CheckAuditLogAsync(client);
            await CheckListingAsync(client);
            await CheckMovesAsync(client);
            await CheckIngestionAsync(ingestClient);
        }
        finally
        {
            int removed = await RemoveAsync(journal, client) + await RemoveAsync(journal, ingestClient)
                + await RemoveAsync(models, ProfileKey.ClientHashOf(client)) + await RemoveAsync(models, ProfileKey.ClientHashOf(ingestClient));
            Console.WriteLine($"Removed the {removed} blobs and folders the check made.");
        }

        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return failures;
    }

    private async Task CheckProfilesAsync(string client)
    {
        var store = new BlobModelStore(models);
        var key = new ProfileKey(client, Customer, CardA);
        CardProfile profile = CardProfile.Build(Symbols(14), new ScoringOptions());
        string v1 = await store.PutAsync(key, profile, null);
        StoredProfile? read = await store.GetAsync(key);
        bool createRefused = await RefusedAsync(() => store.PutAsync(key, profile, null));
        string v2 = await store.PutAsync(key, profile, v1);
        bool staleRefused = await RefusedAsync(() => store.PutAsync(key, profile, v1));
        Report("C2", "profiles: written, read back with the same version, and a second create and a stale replace refused",
            read is not null && read.Version == v1 && CardProfileFile.Write(read.Profile).AsSpan().SequenceEqual(CardProfileFile.Write(profile))
            && createRefused && v2 != v1 && staleRefused);
    }

    private async Task CheckHistoriesAsync(string client)
    {
        var store = new BlobHistoryStore(models);
        var key = new ProfileKey(client, Customer, CardA);
        var history = new CardHistory([0, 1, 5], [57, 0, 3]);
        string v1 = await store.PutAsync(key, history, null);
        StoredHistory? read = await store.GetAsync(key);
        bool createRefused = await RefusedAsync(() => store.PutAsync(key, history, null));
        string v2 = await store.PutAsync(key, new CardHistory([0, 1, 5, 6], [57, 0, 3, 9]), v1);
        bool staleRefused = await RefusedAsync(() => store.PutAsync(key, history, v1));
        Report("C3", "histories: written, read back with the same version, and a second create and a stale replace refused",
            read is not null && read.Version == v1 && read.History.Seq.SequenceEqual(history.Seq) && createRefused && v2 != v1 && staleRefused);
    }

    private async Task CheckAuditLogAsync(string client)
    {
        var log = new BlobAuditLog(models);
        var now = DateTimeOffset.UtcNow;
        AuditEntry first = AuditEntry.BadName(now, new string('a', 64));
        AuditEntry second = AuditEntry.Rejected(now, "day1.jsonl", new string('b', 64), 2);
        await log.AppendAsync(client, first);
        await log.AppendAsync(client, second);
        BlobClient blob = models.GetBlobClient(BlobAuditLog.BlobNameFor(client));
        byte[] stored = (await blob.DownloadContentAsync()).Value.Content.ToArray();
        string type = (await blob.GetPropertiesAsync()).Value.ContentType;
        Report("C4", "audit log: two lines appended, byte for byte, with content type application/x-ndjson",
            stored.AsSpan().SequenceEqual([.. AuditFile.WriteLine(first), .. AuditFile.WriteLine(second)]) && type == BlobAuditLog.ContentType);
    }

    private async Task CheckListingAsync(string client)
    {
        await PutAsync($"{client}/b.jsonl", "b");
        await PutAsync($"{client}/a.jsonl", "a");
        await PutAsync($"{client}/B.jsonl", "B");
        await PutAsync($"{client}/x.jsonl.part", "x");
        await PutAsync($"{client}/archive/old.jsonl", "old");
        await PutAsync($"{client}/d.jsonl/inner.txt", "inside a folder named like a journal file");

        // How this account lists the folder named d.jsonl, for the record.
        string seen = "not listed as a blob";
        await foreach (BlobHierarchyItem item in journal.GetBlobsByHierarchyAsync(BlobTraits.Metadata, BlobStates.None, "/", client + "/", CancellationToken.None))
        {
            if (item.IsBlob && item.Blob.Name == $"{client}/d.jsonl")
            {
                seen = "listed as a blob of " + item.Blob.Properties.ContentLength + " bytes, hdi_isfolder="
                    + (item.Blob.Metadata.TryGetValue(DirectoryMetadata, out string? value) ? value : "(none)");
            }
        }

        Console.WriteLine($"INFO C5 the folder d.jsonl is {seen}.");
        JournalFileRef[] files = [.. (await new BlobJournalSource(journal).ListAsync()).Where(f => f.ClientId == client)];
        bool versions = true;
        foreach (JournalFileRef file in files)
        {
            versions &= file.Version == (await journal.GetBlobClient($"{client}/{file.Name}").GetPropertiesAsync()).Value.ETag.ToString();
        }

        Report("C5", "listing: only B.jsonl, a.jsonl and b.jsonl, in that order, each with its blob's ETag (not x.jsonl.part, archive/ or the folder d.jsonl)",
            files.Select(f => f.Name).SequenceEqual(["B.jsonl", "a.jsonl", "b.jsonl"]) && versions);
    }

    private async Task CheckMovesAsync(string client)
    {
        var source = new BlobJournalSource(journal);
        JournalFileRef[] files = [.. (await source.ListAsync()).Where(f => f.ClientId == client)];
        string name = await source.MoveAsync(files.Single(f => f.Name == "a.jsonl"), JournalFolder.Archive, "old.jsonl");
        bool moved = name == "old.1.jsonl"
            && await TextAsync($"{client}/archive/old.1.jsonl") == "a"
            && await TextAsync($"{client}/archive/old.jsonl") == "old"
            && await TextAsync($"{client}/a.jsonl") is null;
        Report("C6", "move: copied to archive/old.1.jsonl because old.jsonl was taken, nothing overwritten, original deleted", moved);

        JournalFileRef b = files.Single(f => f.Name == "b.jsonl");
        await PutAsync($"{client}/b.jsonl", "b, replaced");
        bool refused = false;
        try
        {
            await source.MoveAsync(b, JournalFolder.Archive, "b.jsonl");
        }
        catch (IOException)
        {
            refused = true;
        }

        Report("C7", "move: a file replaced after it was listed is not moved, and the replacement stays",
            refused && await TextAsync($"{client}/b.jsonl") == "b, replaced" && await TextAsync($"{client}/archive/b.jsonl") is null);
    }

    private async Task CheckIngestionAsync(string client)
    {
        string journalText = SampleJournal();
        await PutAsync($"{client}/day1.jsonl", journalText);
        var profiles = new BlobModelStore(models);
        var histories = new BlobHistoryStore(models);
        JournalIngestor Ingestor() => new(new OneClient(new BlobJournalSource(journal), client), histories, profiles, new BlobAuditLog(models), new ScoringOptions());

        AuditEntry first = (await Ingestor().RunAsync()).Single().Entry;
        var key = new ProfileKey(client, Customer, CardA);
        StoredHistory? history = await histories.GetAsync(key);
        StoredProfile? profile = await profiles.GetAsync(key);
        Report("C8", "ingestion: the sample journal archived, 15 transactions new, card A trained on its 14",
            first.Outcome == AuditOutcome.Archived && first.Records == 15 && first.New == 15 && first.CardsTrained == 1
            && history?.History.Count == 14 && profile?.Profile.HistoryLength == 14
            && await TextAsync($"{client}/archive/day1.jsonl") == journalText && await TextAsync($"{client}/day1.jsonl") is null);

        await PutAsync($"{client}/day1.jsonl", journalText);
        AuditEntry second = (await Ingestor().RunAsync()).Single().Entry;
        Report("C9", "ingestion: the same file sent again changes nothing and is archived as day1.1.jsonl",
            second.Outcome == AuditOutcome.Archived && second.New == 0 && second.CardsTrained == 0
            && (await histories.GetAsync(key))?.Version == history?.Version && (await profiles.GetAsync(key))?.Version == profile?.Version
            && await TextAsync($"{client}/archive/day1.1.jsonl") == journalText);
    }

    // ---- list and ingest --------------------------------------------------------------------------

    public async Task<int> ListAsync()
    {
        await foreach (BlobHierarchyItem top in journal.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", null, CancellationToken.None))
        {
            if (!top.IsPrefix)
            {
                Console.WriteLine($"{Shown(top.Blob.Name, JournalNames.IsValidFileName)}  (a file outside any client folder)");
                continue;
            }

            string client = top.Prefix[..^1];
            Console.WriteLine($"{Shown(client, JournalNames.IsValidClientId)}/");
            await ListFolderAsync(top.Prefix, "  ");
        }

        return 0;
    }

    private async Task ListFolderAsync(string prefix, string indent)
    {
        await foreach (BlobHierarchyItem item in journal.GetBlobsByHierarchyAsync(BlobTraits.Metadata, BlobStates.None, "/", prefix, CancellationToken.None))
        {
            if (item.IsPrefix)
            {
                string folder = item.Prefix[prefix.Length..^1];
                bool known = folder is FolderJournalSource.ArchiveFolderName or FolderJournalSource.QuarantineFolderName;
                Console.WriteLine($"{indent}{(known ? folder : "(a folder the server does not use)")}/");
                if (known)
                {
                    await ListFolderAsync(item.Prefix, indent + "  ");
                }

                continue;
            }

            string name = item.Blob.Name[prefix.Length..];
            bool directory = item.Blob.Metadata.TryGetValue(DirectoryMetadata, out string? value) && value == "true";
            Console.WriteLine($"{indent}{Shown(name, JournalNames.IsValidFileName)}  {(directory ? "directory entry" : item.Blob.Properties.ContentLength + " bytes")}");
        }
    }

    public async Task<int> IngestAsync()
    {
        var ingestor = new JournalIngestor(
            new BlobJournalSource(journal), new BlobHistoryStore(models), new BlobModelStore(models), new BlobAuditLog(models), new ScoringOptions());
        IReadOnlyList<IngestedFile> handled = await ingestor.RunAsync();
        foreach (IngestedFile file in handled)
        {
            AuditEntry e = file.Entry;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{file.ClientId}  {e.File ?? "(name withheld)"}  {e.Outcome}  records={e.Records} new={e.New} quarantined={e.Quarantined.Count} cards_trained={e.CardsTrained} cards_failed={e.CardsFailed}"));
        }

        Console.WriteLine($"{handled.Count} file(s) handled.");
        return 0;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    public static string SampleJournal()
    {
        var text = new StringBuilder();
        for (int seq = 0; seq < 14; seq++)
        {
            text.Append(Line(seq, CardA)).Append('\n');
        }

        text.Append(Line(0, CardB)).Append('\n');
        return text.ToString();
    }

    private static string Line(int seq, string card) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"seq\":{seq},\"transaction_token\":\"tok_txn_{seq:x24}\",\"customer_token\":\"{Customer}\",\"card_token\":\"{card}\","
        + $"\"amount_bin\":{seq % 3},\"merchant_category\":\"{Quantizer.CategoryNames[seq % 4]}\",\"hour\":{seq * 5 % 24},\"is_online\":{(seq % 5 == 0 ? "true" : "false")}}}");

    private static int[] Symbols(int count) =>
        [.. Enumerable.Range(0, count).Select(seq => Quantizer.ToSymbol(seq % 3, Quantizer.CategoryNames[seq % 4], seq * 5 % 24, seq % 5 == 0))];

    private static string Shown(string name, Func<string, bool> valid) => valid(name) ? name : "(a name that breaks the rules)";

    private bool Report(string id, string what, bool passed)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {id} {what}");
        failures += passed ? 0 : 1;
        return passed;
    }

    private static async Task<bool> RefusedAsync(Func<Task> write)
    {
        try
        {
            await write();
            return false;
        }
        catch (ConcurrencyConflictException)
        {
            return true;
        }
    }

    private Task PutAsync(string name, string content) => journal.GetBlobClient(name).UploadAsync(BinaryData.FromString(content), overwrite: true);

    private async Task<string?> TextAsync(string name)
    {
        try
        {
            return (await journal.GetBlobClient(name).DownloadContentAsync()).Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    // Removes every blob under the folder, deepest first (with a hierarchical namespace a folder is
    // removed only once empty), then the folder itself.
    private static async Task<int> RemoveAsync(BlobContainerClient container, string folder)
    {
        var names = new List<string>();
        await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, folder + "/", CancellationToken.None))
        {
            names.Add(blob.Name);
        }

        names.Add(folder);
        int removed = 0;
        foreach (string name in names.OrderByDescending(n => n.Count(c => c == '/')).ThenByDescending(n => n, StringComparer.Ordinal))
        {
            removed += (await container.GetBlobClient(name).DeleteIfExistsAsync()).Value ? 1 : 0;
        }

        return removed;
    }

    // The ingestor over one client's folder only, so the check never touches anyone else's files.
    private sealed class OneClient(IJournalSource inner, string client) : IJournalSource
    {
        public async Task<IReadOnlyList<JournalFileRef>> ListAsync(CancellationToken cancellationToken = default) =>
            [.. (await inner.ListAsync(cancellationToken)).Where(f => f.ClientId == client)];

        public Task<byte[]> ReadAsync(JournalFileRef file, CancellationToken cancellationToken = default) => inner.ReadAsync(file, cancellationToken);

        public Task<string> MoveAsync(JournalFileRef file, JournalFolder folder, string targetName, CancellationToken cancellationToken = default) =>
            inner.MoveAsync(file, folder, targetName, cancellationToken);
    }
}
