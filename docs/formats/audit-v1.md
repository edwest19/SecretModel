<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the audit log approved by Edwin West on 2026-11-26 (design B); the exact properties and their order approved 2026-11-27. AI-generated. -->

# Ingestion audit log, format `secretmodel-audit` version 1

The file in which journal ingestion records each journal file it handled (README SM-JRNL-008,
§8.2): one line per file, added at the end, never changed afterwards. Written by
`SecretModel.Core.Journal.AuditFile`; the server never reads it back. This document locks the
format. The known-answer test `AuditFileTests.Write_MatchesTheDocumentedBytes` checks the example
below byte for byte.

One audit log per client, at `{model root}/{client hash}/audit.jsonl` (README §8.2), next to the
client's histories and profiles. It names no client: the client hash in the path is the only link.

## Lines

- UTF-8, no byte-order mark. Each line is one JSON object, written without spaces, and ends with
  LF, the last line too.
- Lines are added in the order the files were handled.

## Properties

Every line starts with these four properties, in this order:

| Property | JSON type | Meaning |
|---|---|---|
| `time` | string | When the file was handled, in UTC to the second: `YYYY-MM-DDTHH:MM:SSZ`. |
| `file` | string or `null` | The file's name as it arrived (README §8.2 name rules). `null` for `bad_name`: a name that breaks the rules is never recorded. |
| `sha256` | string | The SHA-256 of the file's bytes, 64 lowercase hex characters. |
| `outcome` | string | `archived`, `retry`, `rejected` or `bad_name`. |

The outcome decides what follows, in this order:

| `outcome` | What happened | Then |
|---|---|---|
| `archived` | Every card was saved; the file moved to `archive/`. | `records`, `new`, `quarantined`, `cards_trained` |
| `retry` | At least one card could not be saved; the file stays where it is and is read again on the next run. | `records`, `new`, `quarantined`, `cards_trained`, `cards_failed` |
| `rejected` | A line may carry a card number (README SM-JRNL-005); the file moved to `quarantine/` unchanged and nothing from it was used. | `line` |
| `bad_name` | The name breaks the rules; the file moved to `quarantine/`, renamed as its SHA-256 with `.jsonl` after it. Its content was not read. | nothing more |

| Property | JSON type | Meaning |
|---|---|---|
| `records` | number | How many lines were transactions: the journal reader's records (`docs/formats/journal-v1.md`), less the lines quarantined as `SeqConflict`. Every line is either one of these or quarantined. |
| `new` | number | How many of them were added to a card's history in this run. A transaction already stored, or repeated in the same file, is not new (README SM-JRNL-003). |
| `quarantined` | array | Each quarantined line as `[line number, reason]`, in line order. The reasons are the reader's (`docs/formats/journal-v1.md`) and `SeqConflict`: a `seq` the card already has, or that other lines in the file give, with a different symbol. |
| `cards_trained` | number | How many cards had their profile built in this run. |
| `cards_failed` | number | How many cards could not be saved. At least 1. |
| `line` | number | The first line that may carry a card number. |

- Numbers are whole numbers written as plain decimal digits.
- A line holds no transaction content (README SM-JRNL-008): no tokens, amounts, categories, hours or
  text from the file, only the counts, line numbers and reasons above.

## Example (known answer)

These 4 lines, exactly 824 bytes, each ending in LF, with SHA-256
`751bb63f1a3a3164ba9e40e7d7b834a610b2fba7b22d579528a849e9950db692`:

```
{"time":"2026-11-26T20:15:03Z","file":"day1.jsonl","sha256":"1771857b18f0e12adf90d603a0e1bca04eb3eb3ef1191432c8306d42563a1852","outcome":"archived","records":3,"new":3,"quarantined":[[3,"BlankLine"],[4,"InvalidMerchantCategory"],[5,"InvalidHour"],[6,"UnknownProperty"]],"cards_trained":0}
{"time":"2026-11-26T20:16:00Z","file":"day2.jsonl","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outcome":"retry","records":20,"new":6,"quarantined":[[7,"SeqConflict"]],"cards_trained":1,"cards_failed":1}
{"time":"2026-11-26T20:17:30Z","file":"day3.jsonl","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","outcome":"rejected","line":2}
{"time":"2026-11-26T20:18:45Z","file":null,"sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","outcome":"bad_name"}
```

Line 1 is what ingesting the journal format's own example (`docs/formats/journal-v1.md`, whose
SHA-256 it shows) as `day1.jsonl` into empty stores records; the test
`JournalIngestorTests.TheDocumentedJournal_IsArchived_WithTheDocumentedAuditLine` checks that. The
hashes on lines 2 to 4 are made up. The byte count and SHA-256 were computed with Python
(`json.dumps` with `separators=(",", ":")`), not with the C# under test.
