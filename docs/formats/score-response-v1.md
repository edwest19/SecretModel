<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-27, from the design approved by Edwin West the same day (Task 9, decision 2, and the Task 4 answers). AI-generated. -->

# Score response, version 1

The body of a `200` answer to `POST /v1/score` (README §3.2). The server writes it with
`SecretModel.Api.Scoring.ScoreResponse`. This document locks the format. The known-answer test
`ScoreResponseTests.Write_TheDocumentedExamples_GiveTheDocumentedBytes` checks the examples below
byte for byte.

## Body

- UTF-8, with no byte-order mark, no whitespace and no newline at the end.
- `Content-Type: application/json; charset=utf-8`.
- One JSON object with exactly these nine properties, always all of them, in this order:

| Property | JSON type | Meaning (README §7) |
|---|---|---|
| `transaction_token` | string | The request's `transaction_token`, unchanged. A valid `tok_txn_` token. |
| `fraud_score` | number | 0 to 1 (SM-SCORE-008, SM-SCORE-011). 0.5 at cold start. |
| `verdict` | string | `LEGIT`, `REVIEW` or `FRAUD_SUSPECTED` (SM-SCORE-006). |
| `confidence` | number | 0 to 1: the highest `fraud_score` the card's profile can give (SM-SCORE-014). 0 at cold start. |
| `anomaly_factors` | array of strings | Each at most once, in this order: `cold_start_no_history`, `window_in_unusual_tail`, `below_all_baseline_windows`, `symbol_not_in_history`. Can be empty. |
| `window_log_likelihood` | number or `null` | The scored window's log-likelihood per symbol (SM-SCORE-002). `null` at cold start. |
| `baseline_tail` | number or `null` | p: the share of the card's baseline windows at most as likely, (1 + count) / (n + 1) (SM-SCORE-011). `null` at cold start. |
| `expected_range` | array of 2 numbers, or `null` | The 5th and 95th percentiles, nearest rank, of the card's baseline (SM-SCORE-009). `null` at cold start. |
| `viterbi_path` | array of whole numbers | The most likely hidden state for each transaction in the scored window, oldest first (SM-SCORE-009). Empty (`[]`) at cold start. |

At cold start (the card has no profile, SM-SCORE-005), `cold_start_no_history` is the only anomaly
factor.

## Numbers

A number is written as .NET's `System.Text.Json` writes a `double`: the shortest text that reads
back as the same `double`. So:

- a whole value has no fraction or exponent: `1`, `0`, `-3`, not `1.0`;
- an exponent is written `E`, a sign and at least two digits: `5E-05`, `1E+17`. .NET uses one when
  the size of the number (ignoring its sign) is below 0.0001 or at least 1E+17 (measured on .NET
  10.0.15: `0.0001` but `9.999E-05`; `99990000000000000` but `1E+17`);
- the digits are the same as Python's `repr` gives, but Python writes `1.0` and `5e-05`. Both are
  valid JSON for the same number, so **a client must compare numbers, not text.**

Every number is finite. With the profiles the server trains, every probability stays above 0
(emission smoothing, SM-SCORE-013), so no log-likelihood is −∞. A profile that would give a number
that isn't finite (only possible with a profile file made by hand) gets `500` instead of this body.

## Examples (known answer)

Byte counts and SHA-256 hashes were computed by Python from these exact texts, not by the C# under
test. The numbers' digits match Python's `repr`. All tokens are made up.

**A.** A warm card, `FRAUD_SUSPECTED`: 320 bytes, SHA-256
`d05775c43ec828477766e3e884b206c49d86f17f957f15a67b467e09e43f4f93`.

```json
{"transaction_token":"tok_txn_feedfacefeedfacefeed0001","fraud_score":1,"verdict":"FRAUD_SUSPECTED","confidence":0.999,"anomaly_factors":["window_in_unusual_tail","below_all_baseline_windows","symbol_not_in_history"],"window_log_likelihood":-9.375,"baseline_tail":5E-05,"expected_range":[-6.5,-2.125],"viterbi_path":[3]}
```

**B.** A warm card with a window of 4, `LEGIT`, no anomaly factor: 250 bytes, SHA-256
`17e2472c749450527a9fd46baba7b30f015dae511f0658d63457e7cd10a7cac4`.

```json
{"transaction_token":"tok_txn_feedfacefeedfacefeed0002","fraud_score":0,"verdict":"LEGIT","confidence":0.3333333333333333,"anomaly_factors":[],"window_log_likelihood":-3.0625,"baseline_tail":0.6,"expected_range":[-5.5,-1.75],"viterbi_path":[0,2,2,1]}
```

**C.** Cold start: 242 bytes, SHA-256
`0f0fecc3c5c858de4837dac0f4e33e18f8c30674e622a7fde6087f8327af60d3`.

```json
{"transaction_token":"tok_txn_feedfacefeedfacefeed0003","fraud_score":0.5,"verdict":"REVIEW","confidence":0,"anomaly_factors":["cold_start_no_history"],"window_log_likelihood":null,"baseline_tail":null,"expected_range":null,"viterbi_path":[]}
```

Example C is also the whole answer the server gives for a card it has no profile for, with default
settings; `ServerScoreTests` checks it over real TLS.
