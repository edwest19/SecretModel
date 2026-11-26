<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26, from the format approved by Edwin West the same day. AI-generated. -->

# Card history file, format `secretmodel-history` version 1

The file that keeps one card's symbol history (README §9.3): every transaction ingested for the
card, as its sequence number and its observation symbol. It is kept so that the card can be
retrained on its full history when more transactions arrive; the profile itself keeps only what
scoring needs. Written and read by `SecretModel.Core.Scoring.CardHistoryFile`. This document locks
the format. The known-answer test `CardHistoryFileTests.Write_MatchesTheDocumentedBytes` checks the
example below byte for byte.

## Encoding

- UTF-8 JSON, no byte-order mark, a single line, no trailing newline.
- Exactly one JSON object with exactly these four properties, in this order:

| Property | JSON type | Meaning |
|---|---|---|
| `format` | string | Always `"secretmodel-history"`. |
| `version` | number | Always `1`. |
| `seq` | array of 1 or more whole numbers | The transactions' sequence numbers (journal `seq`), strictly ascending, each 0 to 999,999,999,999. |
| `symbols` | array of whole numbers | Exactly as many as `seq`: `symbols[i]` is the observation symbol of transaction `seq[i]`, each 0 to 511 (README SM-QNT-001). |

- **Whole numbers** are written as plain decimal digits, with no sign, fraction or exponent. A
  reader accepts nothing else.

The file holds only sequence numbers and observation symbols (README SM-PRIV-003). It holds no
tokens, no client identifier, no amounts and no text: its only string is the format name. Where it
is stored is README §9.3.

## Reading

A reader rejects the file (in C#, `InvalidDataException`) when:

- it is not valid JSON, or the root is not an object;
- any property is missing, extra, repeated or out of order;
- `format` or `version` differs;
- `seq` or `symbols` is not an array of whole numbers written as plain digits;
- or any rule above fails, such as `seq` being empty or out of order, the two arrays having
  different lengths, or a number being out of range.

## Example (known answer)

A history of four transactions:

| `seq` | 0 | 1 | 5 | 999,999,999,999 |
|---|---|---|---|---|
| `symbols` | 57 | 0 | 3 | 511 |

is written as exactly these 94 bytes, with SHA-256
`e06ef9afcf59c80c3967ec92fc4effa6ff4674845e318ae91c9adfcfe31c78fb`:

```
{"format":"secretmodel-history","version":1,"seq":[0,1,5,999999999999],"symbols":[57,0,3,511]}
```

The byte count and SHA-256 were computed with Python, not with the C# under test.
