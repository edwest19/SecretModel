<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, for Task 8c step 6, from the design approved by Edwin West on 2026-11-27. AI-generated. -->

# Azure storage: setup and check

**Status: not run.** Paused by Edwin on 2026-11-27 because of the cost; the server is run on his
PC in Home mode first. Azure mode is built and tested against Azurite only (README §8.3, §9.2).
These steps and `tools/SecretModel.AzureCheck` are kept for when Azure mode is deployed.

This sets up the storage account that Azure mode uses (README §1, §8.3, §9.2) and checks the
Azure-mode stores and journal source against it, for what Azurite can't show: a hierarchical
namespace, and files that arrive through the SFTP endpoint. Edwin does every step in the Azure
portal; nothing secret is written to this repository.

Portal labels are taken from Microsoft's documentation and may differ. If a step doesn't match
what the portal shows, stop and note what it shows instead.

**Cost:** the SFTP endpoint is charged by the hour for as long as it is enabled, whether or not
anyone connects. Microsoft's Q&A answers have given $0.30 an hour; the current rate is on the Azure
Blob Storage pricing page. Part 6 turns it off again. The account itself costs very little while
it holds only test data.

Throughout, `<account>` is the storage account's name.

## Part 1. The storage account

1. **Create a storage account.**
   - Basics: a new resource group (for example `secretmodel-rg`); a name of your choice, 3 to 24
     lowercase letters and digits, unique across Azure; a region near you; primary service
     *Azure Blob Storage or Azure Data Lake Storage Gen 2*; performance *Standard*; redundancy
     *Locally-redundant storage (LRS)*.
   - Advanced: tick **Enable hierarchical namespace**. Leave **Enable SFTP** unticked for now.
   - Review and create.
2. **Containers:** under Data storage, Containers, add `journal` and `models`, both with anonymous
   access off (private). The server never creates a container.
3. **Let the clients reach their folders:** on the `journal` container, open **Manage ACL** and,
   under access permissions, give **Other** *Execute* only (no Read, no Write). A local user needs
   Execute on the container's root folder to reach a folder inside it; without Read it still can't
   list the root.

## Part 2. SFTP and local users

4. Under Settings, **SFTP**: enable SFTP. The hourly charge starts now.
5. **Add local user** `setupadmin`, used once to create the client folders and then deleted.
   - Authentication: *SSH Password*. Copy the password into your password manager: Azure shows it
     only once.
   - Permissions: container `journal`, with every permission (Read, Write, Delete, List, Create,
     Modify Ownership, Modify Permissions). Only a local user can make another local user a
     folder's owner, and it needs Modify Ownership to do so.
   - Home directory: `journal`.
6. **Add local user** `clienta`.
   - Authentication: *SSH Password*; copy it.
   - Permissions: **no container permissions**. Tick **Allow ACL authorization**. With container
     permissions it could reach every client's folder.
   - Home directory: `journal/client-a`.
7. **Add local user** `clientb` the same way, with home directory `journal/client-b`.
8. Reopen `clienta` and `clientb` (Edit local user) and note each one's **User ID**.

## Part 3. The client folders

9. Connect as `setupadmin` (Windows PowerShell):
   ```
   cd C:\Users\edwes\source\repos\SecretModel; sftp <account>.setupadmin@<account>.blob.core.windows.net
   ```
   On the first connection, compare the host key fingerprint with Microsoft's published SFTP host
   keys for your region before accepting it.
10. At the `sftp>` prompt, one line at a time, with the User IDs from step 8:
    ```
    mkdir client-a
    mkdir client-b
    chown <clienta User ID> client-a
    chown <clientb User ID> client-b
    chmod 700 client-a
    chmod 700 client-b
    ls -l
    bye
    ```
    `ls -l` should show each folder owned by its client's User ID with `drwx------`.
11. In the portal, **delete the local user `setupadmin`**.

## Part 4. The check program

12. Under Security + networking, **Shared access signature**: allowed services *Blob*; resource
    types *Container* and *Object*; permissions *Read, Write, Delete, List, Add, Create*; start now
    and expiry tomorrow; HTTPS only. Generate it and copy the **Connection string**. It is a secret:
    never paste it into a chat, a file or a commit.
13. Put it in this PowerShell window only, then clear the clipboard:
    ```
    cd C:\Users\edwes\source\repos\SecretModel; $env:SECRETMODEL_AZURE_STORAGE = Get-Clipboard; Set-Clipboard -Value 'cleared'
    ```
14. Run the checks. They use two test clients of their own and remove everything they made:
    ```
    cd C:\Users\edwes\source\repos\SecretModel; dotnet run -c Release --project tools/SecretModel.AzureCheck -- check
    ```
    The output holds no secret and no name that breaks the rules; it can be pasted into the chat.

## Part 5. SFTP between clients

15. Write a made-up journal file to upload:
    ```
    cd C:\Users\edwes\source\repos\SecretModel; dotnet run -c Release --project tools/SecretModel.AzureCheck -- sample day1.jsonl
    ```
16. As `clienta`:
    ```
    cd C:\Users\edwes\source\repos\SecretModel; sftp <account>.clienta@<account>.blob.core.windows.net
    ```
    then at `sftp>`: `pwd`, `put day1.jsonl`, `ls -l`, `ls ..`, `ls ../client-b`, `bye`.
    Expected: `put` and `ls -l` work; `ls ..` and `ls ../client-b` are refused.
17. As `clientb` the same way, then at `sftp>`: `put day1.jsonl`, `ls -l`,
    `get ../client-a/day1.jsonl b-got-a.jsonl`, `mkdir ../client-a/x`, `bye`.
    Expected: the `get` and the `mkdir` are refused.
18. Ingest both files, looking at the container before and after:
    ```
    cd C:\Users\edwes\source\repos\SecretModel; dotnet run -c Release --project tools/SecretModel.AzureCheck -- list
    ```
    ```
    cd C:\Users\edwes\source\repos\SecretModel; dotnet run -c Release --project tools/SecretModel.AzureCheck -- ingest
    ```
    ```
    cd C:\Users\edwes\source\repos\SecretModel; dotnet run -c Release --project tools/SecretModel.AzureCheck -- list
    ```
    Expected: each client's `day1.jsonl` archived, with 15 transactions and one card trained.

## Part 6. Afterwards

19. Under Settings, SFTP: **disable SFTP**. This stops the hourly charge; the local users are kept.
20. Remove the connection string from the window (the signature also expires by itself):
    ```
    cd C:\Users\edwes\source\repos\SecretModel; Remove-Item Env:SECRETMODEL_AZURE_STORAGE
    ```
21. Keep the account: Task 9 deploys the server against it.
