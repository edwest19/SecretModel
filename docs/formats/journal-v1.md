<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-26, from the record approved by Edwin West the same day. AI-generated. -->

# Journal file, format `secretmodel-journal` version 1

The file a client places in the landing zone to train its cards (README §8). The server only reads
journal files, with `SecretModel.Core.Journal.JournalFile`; it never writes one. This document
locks the format. The known-answer test
`JournalFileTests.Read_TheDocumentedExample_GivesTheDocumentedResult` reads the example below byte
for byte.

A journal file names no client. The client comes from the folder the file is in, never from its
contents (README SM-AUTH-003), so a `client_id` property is an unknown property like any other.

## Lines

- UTF-8, with no byte-order mark. One record per line (README SM-JRNL-001).
- A line ends with LF. A CR just before the LF is removed, so a file with CRLF line endings reads
  the same as one with LF.
- The last line doesn't need a newline. A newline at the very end of the file doesn't start
  another line, and an empty file has no lines.
- Lines are numbered from 1.

## Records

Each line is one JSON object with exactly these eight properties, in any order, each once:

| Property | JSON type | Rule |
|---|---|---|
| `seq` | number | A whole number from 0 to 999,999,999,999 (12 digits at most). The order of a card's transactions. |
| `transaction_token` | string | A `tok_txn_` token. |
| `customer_token` | string | A `tok_cus_` token. |
| `card_token` | string | A `tok_card_` token. |
| `amount_bin` | number | A whole number from 0 to 7 (README SM-QNT-005). How an amount becomes a bin is README OPEN-7. |
| `merchant_category` | string | Exactly one of `grocery`, `gas`, `restaurant`, `retail`, `travel`, `online_market`, `atm`, `other`. |
| `hour` | number | A whole number from 0 to 23 (README SM-QNT-003). |
| `is_online` | `true` or `false` | Whether the transaction was online. |

- **Whole numbers** are plain decimal digits: no sign, fraction or exponent. So `14.0`, `1e1` and
  `-0` are not whole numbers here. (JSON itself forbids leading zeros.)
- **Tokens** follow README SM-PRIV-001: the whole string is `tok_`, the kind, `_`, then exactly 24
  lowercase hex digits. A token of another kind is invalid in that property.
- **Categories** must match exactly, in lowercase, so `Grocery` is invalid. An unknown category is
  not read as `other`, because the archive would otherwise keep free text (README SM-PRIV-003).
- **Strings** are read as JSON defines them, so `"grocery"` is `grocery`.
- There is no `country_code`: the model doesn't use it.

A record holds only tokens, an amount bin, a category name, an hour and a flag (README SM-PRIV-003).

## Reading a line

The reader checks each line in this order. It quarantines the line with the first reason that
applies, and the other lines carry on (README SM-JRNL-002). It reports only the line number and the
reason, never anything from the line itself.

| Reason | When |
|---|---|
| `BlankLine` | The line is empty or holds only spaces and tabs. |
| `InvalidText` | The line is not valid UTF-8. |
| `DuplicateProperty` | The line is JSON, except that an object in it (at any depth) repeats a property name. |
| `NotJson` | The line is not exactly one JSON value. A byte-order mark makes line 1 `NotJson`. |
| `InvalidText` | A string or property name holds an escape that is not whole Unicode text, such as `\ud800`. |
| `NotAnObject` | The value is not an object. |
| `UnknownProperty` | The object has a property that is not in the table above. |
| `MissingProperty` | A property in the table is missing. |
| `InvalidSeq`, `InvalidTransactionToken`, `InvalidCustomerToken`, `InvalidCardToken`, `InvalidAmountBin`, `InvalidMerchantCategory`, `InvalidHour`, `InvalidIsOnline` | That property breaks its rule. They are checked in the table's order. |

## Rejecting a whole file

A file is rejected whole when any line may carry a card number (README SM-PRIV-002, SM-JRNL-005).
No record is read from it and no line is quarantined on its own: there is no partial ingestion. The
reader reports the number of the first such line, never its content.

A line may carry a card number when a text taken from it holds 13 or more consecutive decimal
digits, in any script, after ignoring every character that is not a letter or a digit. Which texts
are scanned depends on whether the line is valid JSON: valid UTF-8, exactly one JSON value, no
repeated property names and every string whole Unicode text.

**A line that is valid JSON.** If the line is an object, each of its property names and each of its
values is scanned on its own. Otherwise the line's value is scanned as a whole.

- A string counts as its decoded text, so an escape such as `4` counts as the digit it stands
  for.
- A string that is a valid token (README SM-PRIV-001, any kind) counts as nothing: tokens are exempt.
  A token can be 24 hex digits that are all decimal digits, which is why `seq` stops at 12 digits.
- A number counts as its text as written, so a 13-digit `seq` rejects the file.
- An array or object counts as one value: everything in it, in order. So `[4111,1111,1111,1111]`
  holds 16 digits in a row.

**Any other line.** Its whole text is scanned, and nothing is exempt, not even something shaped like
a token, because there are no values to tell apart. Each invalid UTF-8 sequence counts as a
character that is neither a letter nor a digit. The text is scanned twice:

- as written;
- with JSON's escapes decoded from left to right, as a JSON reader would: `4` is the digit 4,
  `\n` a newline, and so on. So `\\u0034` is a backslash and then `u0034`. A backslash that starts no
  escape is kept as it is.

So escapes can't hide digits in a broken line either: `4111111\n111111111` holds 16 digits in a row.

Every line is checked this way, including a line that would otherwise be quarantined.

## Example (known answer)

These 7 lines, exactly 1,479 bytes, each ending in LF (the last one too), with SHA-256
`1771857b18f0e12adf90d603a0e1bca04eb3eb3ef1191432c8306d42563a1852`. Line 3 is empty.

```
{"seq":1,"transaction_token":"tok_txn_feedfacefeedfacefeed0001","customer_token":"tok_cus_c0ffeec0ffeec0ffeec0ffee","card_token":"tok_card_cafecafecafecafecafecafe","amount_bin":3,"merchant_category":"grocery","hour":14,"is_online":false}
{"card_token":"tok_card_beefbeefbeefbeefbeefbeef","customer_token":"tok_cus_c0ffeec0ffeec0ffeec0ffee","seq":2,"is_online":true,"hour":23,"merchant_category":"online_market","amount_bin":7,"transaction_token":"tok_txn_feedfacefeedfacefeed0002"}

{"seq":3,"transaction_token":"tok_txn_feedfacefeedfacefeed0003","customer_token":"tok_cus_c0ffeec0ffeec0ffeec0ffee","card_token":"tok_card_cafecafecafecafecafecafe","amount_bin":3,"merchant_category":"Grocery","hour":9,"is_online":false}
{"seq":4,"transaction_token":"tok_txn_feedfacefeedfacefeed0004","customer_token":"tok_cus_c0ffeec0ffeec0ffeec0ffee","card_token":"tok_card_cafecafecafecafecafecafe","amount_bin":2,"merchant_category":"gas","hour":24,"is_online":false}
{"seq":5,"transaction_token":"tok_txn_feedfacefeedfacefeed0005","customer_token":"tok_cus_c0ffeec0ffeec0ffeec0ffee","card_token":"tok_card_cafecafecafecafecafecafe","amount_bin":1,"merchant_category":"restaurant","hour":19,"is_online":false,"country_code":"US"}
{"seq": 999999999999, "transaction_token": "tok_txn_feedfacefeedfacefeed0006", "customer_token": "tok_cus_c0ffeec0ffeec0ffeec0ffee", "card_token": "tok_card_beefbeefbeefbeefbeefbeef", "amount_bin": 0, "merchant_category": "atm", "hour": 0, "is_online": false}
```

read as three records and four quarantined lines:

| Line | Result | Why |
|---|---|---|
| 1 | Record: seq 1, `…0001`, card `…cafe`, bin 3, `grocery`, hour 14, not online | |
| 2 | Record: seq 2, `…0002`, card `…beef`, bin 7, `online_market`, hour 23, online | Properties in another order. |
| 3 | `BlankLine` | |
| 4 | `InvalidMerchantCategory` | `Grocery` is not lowercase. |
| 5 | `InvalidHour` | 24 is out of range. |
| 6 | `UnknownProperty` | `country_code`. |
| 7 | Record: seq 999,999,999,999, `…0006`, card `…beef`, bin 0, `atm`, hour 0, not online | The largest `seq`, with whitespace between JSON tokens. |

Every record has the customer token `tok_cus_c0ffeec0ffeec0ffeec0ffee`. All the tokens here are made
up; none was made from real data.
