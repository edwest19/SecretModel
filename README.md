<!-- Written by Claude (Anthropic) for SecretModel. v0.1 drafted 2026-09-13; v0.2 to v0.16 by Claude Opus 5.5, 2026-11-25 to 2026-11-27. AI-generated. -->

# SecretModel

SecretModel is a test and development fraud-detection service. Each payment card is modeled as its
own Hidden Markov Model. A historical journal of tokenized test debit-card transactions arrives by
SFTP and trains the per-card models. Real-time scoring requests are evaluated against those models
over HTTPS with mutual TLS.

**This is AI-generated code.** Claude (Anthropic) writes the code, tests and documentation; Edwin
West decides what it does and approves every merge. It is for demonstration, testing and comparison,
not production, and it never handles real card or cardholder data.

**Status:** in progress. Implemented so far: the client tokenizer (§4), the quantizer (§5), token
validation and the identifier check (SM-PRIV-001, -002, -006), the HMM core (§6), the scorer
(§7), the profile file, the Home-mode and Azure Blob model stores (§9), the journal file reader
(§8.1), the Home-mode symbol history store (§9.3), Home-mode journal ingestion
with its audit log (§8.2), the Azure-mode journal source (§8.3), the Azure-mode history store
and audit log (§9.2), the Home-mode server's host and certificate login (§3.1), and its scoring
endpoint (§3.2). Not built yet: the server's ingestion loop. A requirement counts as met only when its test has been seen passing *and* seen failing (§12).

**This README is the specification.** It changes in the same pull request as the behavior it
describes.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build SecretModel.slnx -c Release
```

CI (`.github/workflows/build.yml`) builds and tests every pull request and every push to `main` on
ubuntu-latest and windows-latest. A failing test or a compiler warning fails the run. The Blob store
tests also need Azurite, which CI starts (§9.2).

## Repository layout

| Path | What it holds |
|---|---|
| `src/SecretModel.Core` | Logic: tokenizer, quantizer, token validation, HMM, scoring, the profile and history files, the Home-mode model and history stores, the journal file reader, and Home-mode journal ingestion (folder source, ingestor, audit log). No ASP.NET or Azure dependencies. |
| `src/SecretModel.Api` | ASP.NET Core host: the Home-mode server and its client-certificate login (§3.1), and the Azure Blob model store, history store, audit log and journal source. |
| `tests/SecretModel.Tests` | MSTest tests, one or more per requirement. |
| `reference/python` | The Meta AI Python prototype, unmodified. See its `PROVENANCE.md`. |
| `docs/formats` | Locked file formats, each with a known-answer test. |
| `tools/golden` | Produces the tests' golden vectors: by running the Python reference, or by exhaustive exact computation. |
| `tools/unicode` | Measures Python's lowercasing to produce `PythonLowercaseData.g.cs` (SM-TOK-008). |
| `tools/SecretModel.Synthetic` | Synthetic shoppers and injected anomalies, for tests, the study and the harness. |
| `tools/SecretModel.TestCerts` | Makes test certificates for the Home-mode server with .NET's own APIs (§3.1). |
| `tools/SecretModel.AzureCheck` | Checks the Azure-mode stores and journal source against a real storage account (§8.3; `docs/azure/storage-setup.md`). |
| `tools/SecretModel.ScoringStudy` | The measurement behind §7.1. |
| `tools/azurite` | Azurite, Microsoft's Blob Storage emulator, pinned for the tests only (§9.2). |
| `docs/studies` | Full output of each measurement that decided a setting. |

---

# Requirements Specification

**Version:** 0.16
**Date:** 2026-11-27
**Prior art:** Python prototype by Meta AI in consultation with edwest19, kept unmodified in
`reference/python`. This C# work is new and attributed to Claude.

## 0. Purpose and scope

SecretModel models each payment card as its own Hidden Markov Model. A historical journal of
tokenized test transactions trains the per-card models; real-time scoring requests are evaluated
against them over HTTPS with mutual TLS.

The server never receives, derives, or stores a PAN, a cardholder name, or any other PII. All
identifying values are tokenized client-side before transmission.

**Out of scope for v1:** production card data, PCI-DSS certification, real settlement systems,
model retraining scheduling, multi-region failover.

### 0.1 How to read this document

Every requirement has a stable ID. Every requirement must have at least one automated test that
can fail. A requirement with no failing-capable test is not satisfied, regardless of whether the
code appears to implement it.

**Explicit lesson from the prototype:** its CI asserted the string `"Raw PAN detected"` existed in
the source. The assertion passed while the code containing that string was unreachable. Substring
presence is not a test. See §12.

Keywords MUST / MUST NOT / SHOULD follow RFC 2119.

---

## 1. Platform and run modes

SecretModel runs in one of two modes, chosen by configuration. Everything outside §1, §3, §8 and
§9 is identical in both.

| Concern | Home mode (Edwin's network, built first) | Azure mode |
|---|---|---|
| Host | Kestrel on Windows or Linux | Azure App Service, **Linux** |
| TLS termination | Kestrel | App Service front end |
| Client certificate | From the TLS handshake | From the `X-ARR-ClientCert` header, base64 DER |
| Model storage | Local folder | Azure Blob Storage, container `models/` |
| Journal landing | Local folder | Blob Storage SFTP endpoint, container `journal/` |
| Secrets | Environment variables or a local config file excluded by `.gitignore` | App Settings or Key Vault |

Common to both:

| Concern | Decision |
|---|---|
| Runtime | .NET 10, ASP.NET Core |
| Test framework | MSTest |
| Test harness | PowerShell 7 |
| Repository | `edwest19/SecretModel` |

Linux App Service avoids the Windows/IIS failure where large request bodies fail when client
certificates are required. Azure mode uses a single storage account with hierarchical namespace
enabled for both `journal/` and `models/`. Its SFTP endpoint bills hourly while enabled.

---

## 2. Privacy and tokenization (SM-PRIV)

| ID | Requirement |
|---|---|
| SM-PRIV-001 | The server MUST reject any token that does not match `^tok_(cus\|card\|txn\|merch)_[0-9a-f]{24}$` as a whole value. A trailing newline is not allowed (in .NET, `$` would allow one, so the code uses `\z`). Prefix-only checking is insufficient. |
| SM-PRIV-002 | A value that fails SM-PRIV-001 MUST be treated as possibly carrying a card number when, ignoring every character that is not a letter or digit, it contains 13 or more consecutive decimal digits in any script. It is rejected like any malformed token and handled as personal data (SM-PRIV-004, SM-PRIV-005, SM-JRNL-005). **This closes the `tok_4111111111111111` bypass confirmed in the prototype**, and also catches `tok_card_4111…`, dots, slashes, no-break or zero-width spaces between digits, and full-width digits. |
| SM-PRIV-003 | The server MUST NOT persist any value other than: tokens, amount bins, observation symbols, sequence numbers (journal `seq`, at most 12 digits), timestamp buckets, country codes, merchant category codes, and derived model parameters; and, in the ingestion audit log (SM-JRNL-008), file names that follow §8.2, file SHA-256 hashes, times, counts, line numbers and reasons. |
| SM-PRIV-004 | Rejection responses MUST NOT echo the rejected value in the body, headers, or logs. |
| SM-PRIV-005 | No log statement at any level MAY emit a field that failed SM-PRIV-002. |
| SM-PRIV-006 | No identifier in the server source (`src/`) MAY name a PAN-bearing concept (`pan`, `card_number`, `cardholder`, `cvv`, `expiry`). Enforced by a test that splits identifiers into words (`cardNumber` → `card`, `number`) and fails on a forbidden word. A raw substring search is not acceptable, because it matches `Span` and `expand`. |

**Tests:** parameterized negative tests over a set of malformed tokens, including raw PANs in
plain, spaced, hyphenated and `tok_`-prefixed form; a reflection/serialization test asserting the
persisted model type exposes no field outside the SM-PRIV-003 allowlist; a log-capture test.

---

## 3. Authentication and tenant isolation (SM-AUTH)

| ID | Requirement |
|---|---|
| SM-AUTH-001 | Every endpoint except `/health` MUST require a validated client certificate. |
| SM-AUTH-002 | Certificate validation MUST check, at minimum: SHA-256 thumbprint against a configured allowlist, issuer, `NotBefore`/`NotAfter`, and that the certificate parses as well-formed X.509. |
| SM-AUTH-003 | `client_id` MUST be derived from the validated certificate's subject CN, never from a request header, body field, or query parameter. |
| SM-AUTH-004 | In Azure mode, a request bearing an `X-ARR-ClientCert` for a certificate not on the allowlist MUST be rejected with 403. **The service MUST NOT rely on the platform stripping the header.** |
| SM-AUTH-005 | A request with no client certificate MUST be rejected with 401. |
| SM-AUTH-006 | **Tenant isolation.** Client A MUST NOT train, score, read, or enumerate any model belonging to client B, even when both present an identical `card_token`. |
| SM-AUTH-007 | App Service MUST be configured `clientCertEnabled=true`, `clientCertMode=Required`, with `clientCertExclusionPaths` covering only `/health`. |
| SM-AUTH-008 | In Home mode, Kestrel MUST ask for a client certificate during the TLS handshake and allow a connection without one, so `/health` works and other requests get 401. Only a certificate presented in the handshake counts, and it MUST pass SM-AUTH-002. |
| SM-AUTH-009 | In Home mode, the service MUST ignore `X-ARR-ClientCert` entirely. A request whose only certificate is in that header MUST be rejected exactly as if it carried no certificate. |
| SM-AUTH-010 | The run mode MUST come from configuration only, never from a request. An absent or unrecognized mode MUST start in Home mode, which never trusts the header. |
| SM-AUTH-011 | In Azure mode, a request that presents certificate X in the TLS handshake and an `X-ARR-ClientCert` header for a different, allowlisted certificate Y MUST be rejected or authenticated as X, never as Y. Verified against a deployed App Service. See OPEN-5. |

**Tests:** integration tests with real self-signed certificate pairs; a forged-header test that
sends a valid-looking public certificate with no proof of possession and asserts 403; a
cross-tenant test where two clients train the same card token and each sees only its own model; a
Home-mode test proving the header is ignored.

### 3.1 Home-mode server

The Home-mode server runs from a PowerShell window:

```powershell
dotnet run -c Release --project src/SecretModel.Api --no-launch-profile
```

`--no-launch-profile` keeps the template's launch profile from setting other addresses. The server
always runs as `Production`, so no page ever shows exception details.

**Settings**, in the `SecretModel` section:

| Setting | Meaning |
|---|---|
| `Mode` | `Home`. Absent or unrecognized also means Home (SM-AUTH-010). `Azure`, in any case, stops the server: Azure mode is not built yet. |
| `Url` | `https://localhost:7443` by default. An `https` address with no path, whose host is `localhost` or an IP address. `0.0.0.0` listens on every address of the computer. |
| `ServerCertificate` | Path of the server's `.pfx`. Its password comes from the environment variable `SECRETMODEL_CERT_PASSWORD`, never from a file. |
| `ClientCertificateAuthority` | Path of the `.cer` of the authority that issues the client certificates. |
| `AllowedClientThumbprints` | The SHA-256 thumbprints (64 hex characters, any case) of the client certificates let in. At least one. |
| `ModelFolder` | The folder of the card profiles (§9.1). It must already exist: the server never creates it, so a mistyped path stops the server instead of making every card cold-start. |

The committed `appsettings.json` holds no secrets and no paths. The real values go in a JSON file
outside the repository, named by the environment variable `SECRETMODEL_SETTINGS`; environment
variables such as `SecretModel__Url` override both. Every setting is checked before the server
starts. A wrong one stops it with a message that names the setting and never shows the password.

**Certificates.** `tools/SecretModel.TestCerts` makes a test set with .NET's own APIs, no packages:

```powershell
dotnet run -c Release --project tools/SecretModel.TestCerts -- <folder> [client id ...]
```

Into a new or empty folder outside any git repository, it writes `ca.cer` (the test authority),
`server.pfx` (for `localhost`, `127.0.0.1`, `::1` and the computer's name), and `<client id>.pfx`
and `<client id>.cer` for each client (`client-a` and `client-b` if none are given), then prints
their SHA-256 thumbprints. RSA 2048 with SHA-256, valid for a year. The `.pfx` files use the
password in `SECRETMODEL_CERT_PASSWORD`. The authority's private key is not kept, so more
certificates means a new set. A client trusts the server by pinning its thumbprint, so the test
authority is never added to Windows' trusted roots.

**Login.** Kestrel asks for a client certificate during the TLS handshake, and the handshake
finishes with or without one, so the server answers with a status code instead of dropping the
connection. (Measured on .NET 10.0.15: with `AllowCertificate` and no validation callback, a
certificate that doesn't chain to a trusted root ends the connection.) Then, for every path except
`/health`:

1. No certificate from the handshake: 401 `{"error":"certificate_required"}` (SM-AUTH-005). The
   `X-ARR-ClientCert` header is never read (SM-AUTH-009).
2. The checks of SM-AUTH-002, in this order. The first that fails gives 403
   `{"error":"certificate_not_accepted"}`.
   - Its SHA-256 thumbprint is on the allowlist.
   - It chains to the configured authority, which is the only root trusted (not the computer's
     trusted roots), and it is not the authority itself. No revocation check, because the test
     authority publishes no revocation list, and no network calls.
   - The time now is within its `NotBefore` and `NotAfter`, both included, and within the
     authority's.
   - Its subject has exactly one CN, in a name of its own, and the CN is a client id (§8.2). That CN
     is the client id (SM-AUTH-003).

   *Well-formed X.509:* the TLS layer parses the certificate before any of these checks, and one
   that doesn't parse ends the handshake, so the server never sees it. There is no separate check,
   because it could not change any answer.
3. A certificate that fails is logged as one warning with the reason and the first 12 characters of
   its SHA-256 thumbprint, never its subject: a CN that breaks the rules may hold a card number.
   12 characters can't hold 13 digits in a row, so the log guard never withholds the line (about
   4.4% of full thumbprints would be withheld, by exact count). Approved by Edwin.

**Answers.** `GET /health` is 200 `{"status":"ok"}`, with or without a certificate (SM-OPS-001).
After login, an unknown path is 404 `{"error":"not_found"}` and a wrong method is 405
`{"error":"method_not_allowed"}`. Any exception is 500 `{"error":"internal"}`, with no detail. No
error answer repeats what the client sent (SM-PRIV-004), and there is no `Server` header.

**Logs** go to the console; `Microsoft.*` logs only warnings and errors. Every entry, at every
level, passes through a guard before it is written (SM-PRIV-005):

- an exception is written as its type name only, because its message can hold request data;
- an entry whose text then holds 13 or more digits in a row, by the rule of SM-PRIV-002, is written
  as `log entry withheld`;
- scopes are dropped, because ASP.NET Core's hold the request path.

The server writes no log entry for a request it answers, only for a refused certificate and for an
exception.

### 3.2 Scoring endpoint

`POST /v1/score` scores one transaction for the client named by the certificate (§3.1). Design
approved by Edwin on 2026-11-27.

**Request.** `Content-Type: application/json`, with no parameter or with `charset=utf-8` (any
case), and a body of at most 4,096 bytes: one JSON object with the seven properties of a journal
record except `seq` (`transaction_token`, `customer_token`, `card_token`, `amount_bin`,
`merchant_category`, `hour`, `is_online`), each once, in any order, by exactly the rules of
`docs/formats/journal-v1.md`. So `seq`, `client_id` or any other property is unknown. The body is
read by `JournalFile.ReadTransaction`, which applies the journal's checks in the journal's order.

**Answers,** checked in this order:

| Status | Body | When |
|---|---|---|
| 415 | `{"error":"unsupported_media_type"}` | Any other `Content-Type`, or none. |
| 413 | `{"error":"request_too_large"}` | The body is longer than 4,096 bytes, with or without a `Content-Length`. No more than 4,097 bytes are read. |
| 400 | `{"error":"invalid_request","reason":"PossibleCardNumber"}` | Anything in the body may hold a card number (SM-PRIV-002), checked before any other rule, as in a journal line (SM-PS-004). |
| 400 | `{"error":"invalid_request","reason":"…"}` | The first journal rule the body breaks, by its name in `docs/formats/journal-v1.md` (`BlankLine`, `NotJson`, `UnknownProperty`, `InvalidHour` and so on). An empty body is `BlankLine`. |
| 200 | The score, format `docs/formats/score-response-v1.md` | Otherwise. |

**Scoring.** The profile is the one stored for (this client, `customer_token`, `card_token`) in
`ModelFolder` (§9.1), read on every request (SM-STOR-003). The key always holds the client from the
certificate, so one client can never reach another's profile, even for the same tokens
(SM-AUTH-006): it gets a cold start, exactly as for a card it has never trained (SM-PS-004). The
transaction's symbol (§5) is scored by `FraudScorer` with the default options (§7), read-only
(SM-SCORE-015). Thresholds have no setting yet. A damaged profile file is 500.

**Answer format** (`docs/formats/score-response-v1.md`, locked with a known answer): nine
properties in a fixed order, `transaction_token`, `fraud_score`, `verdict`, `confidence`,
`anomaly_factors`, `window_log_likelihood`, `baseline_tail`, `expected_range` and `viterbi_path`.
At cold start, `window_log_likelihood`, `baseline_tail` and `expected_range` are `null` and
`viterbi_path` is `[]`. Numbers are written
as .NET writes a `double`, which can differ in text from Python (`1` and `5E-05` against `1.0` and
`5e-05`, measured), so clients compare numbers, not text. Approved by Edwin.

**Latency** (SM-SCORE-010): for a warm card with a 300-transaction history, over real TLS on one
connection, 200 timed requests after 20 to warm up. Measured in Claude's Linux workspace: median
1.49 ms, p95 2.01 ms. The test fails at a p95 of 200 ms or more.

**Tests:** `ReadTransactionTests`, `ScoreResponseTests` (the format's known answer),
`ServerScoreTests` (over real TLS) and `LogGuardTests`.

---

## 4. Client tokenizer (SM-TOK)

Runs client-side. The vault secret never leaves the client.

| ID | Requirement |
|---|---|
| SM-TOK-001 | Tokens MUST be `tok_{domain}_{h}` where `h` is the first 24 hex characters of HMAC-SHA256(secret, `"{domain}:{value}"`). |
| SM-TOK-002 | Tokenization MUST be deterministic: same secret and same input yield the same token across processes and machines. |
| SM-TOK-003 | A different secret MUST yield a different token for the same input. |
| SM-TOK-004 | Domain separation MUST hold: `card`, `cus`, `merch` tokens of the identical input string MUST differ. |
| SM-TOK-005 | A vault secret shorter than 32 bytes MUST be rejected at construction. |
| SM-TOK-006 | PANs MUST be normalized (spaces and hyphens removed) before HMAC, so `4111 1111 1111 1111` and `4111-1111-1111-1111` produce the same token. |
| SM-TOK-007 | The C# tokenizer MUST produce byte-identical tokens to the Python reference in `reference/python` for a shared golden-vector file. |
| SM-TOK-008 | Merchant names (and category names, §5) MUST be lowercased exactly as Python 3.13's `str.lower()` does (Unicode 15.1), on every operating system. The C# uses data measured from Python (`tools/unicode/generate_python_lowercase.py`), not the OS's Unicode tables. Verified against Python's output for every code point, for the context-dependent final-sigma rule next to every code point, and for 400 random mixed strings. |

As in the reference, only card values are stripped of spaces and hyphens, only merchant names are
lowercased, and customer IDs are used exactly as given. The reference has no `txn` tokenizer
method, so none exists yet; one will be added, with its own requirement, when a task needs it.

---

## 5. Quantization (SM-QNT)

| ID | Requirement |
|---|---|
| SM-QNT-001 | `symbol = ((amount_bin * 8 + mcc_group) * 4 + time_bucket) * 2 + is_online`. |
| SM-QNT-002 | The mapping MUST be a bijection onto exactly 0–511 with zero collisions. *(Verified to hold in the Python reference.)* |
| SM-QNT-003 | Time buckets: hour ∈ [0,6) → 0, [6,12) → 1, [12,18) → 2, [18,24) → 3. Hours outside 0–23 MUST be rejected. *(The prototype accepted any hour: −1 became bucket 0 and 25 or 99 became bucket 3.)* |
| SM-QNT-004 | An unrecognized merchant category MUST map to group 7 (`other`), never throw. |
| SM-QNT-005 | `amount_bin` MUST be 0–7 inclusive; out-of-range input is rejected, not clamped silently. |
| SM-QNT-006 | The C# quantizer MUST produce identical symbols to the Python reference across a shared golden-vector file covering all 512 combinations. |

How a transaction amount becomes an `amount_bin` is not specified yet (OPEN-7).

---

## 6. HMM core (SM-HMM)

| ID | Requirement |
|---|---|
| SM-HMM-001 | The forward algorithm MUST use per-timestep scaling. Log-likelihood MUST match a hand-computed value for a 3-state / 3-symbol fixture to within 1e-9. |
| SM-HMM-002 | Baum-Welch log-likelihood MUST be monotonically non-decreasing across iterations on a fixed training set, allowing for rounding of at most 1e-9 × \|log-likelihood\| per iteration. A decrease beyond that indicates an implementation error and MUST fail the build. |
| SM-HMM-003 | Viterbi MUST return the known-optimal state path for a published textbook fixture. |
| SM-HMM-004 | Training MUST be deterministic given a fixed seed: two runs produce bit-identical parameters. Seeds go through SplitMix64, checked against its published reference outputs, not `System.Random`, whose seeded sequence .NET does not promise to keep. |
| SM-HMM-005 | Serialize → deserialize MUST preserve A, B, Pi exactly (bitwise for IEEE-754 doubles). The file format is locked in `docs/formats/hmm-model-v1.md`. |
| SM-HMM-006 | After every training iteration, rows of A, rows of B, and Pi MUST each sum to 1.0 within 1e-9. |
| SM-HMM-007 | A 10,000-symbol sequence MUST score without underflow, overflow, NaN, or infinity. |
| SM-HMM-008 | `n_states = 5`, `n_symbols = 512` by default, both configurable. |
| SM-HMM-009 | One Baum-Welch update MUST match, within 1e-12, the update computed exactly by enumerating every hidden-state path, over several sequences including a one-symbol sequence. *(The prototype skipped sequences shorter than 2.)* |
| SM-HMM-010 | Invalid input MUST be rejected: empty sequences, symbols outside 0 to `n_symbols − 1`, and parameter rows that are negative, not finite, or don't sum to 1 within 1e-9. A sequence the model can't produce MUST score −∞, never NaN, and can't be trained on. |
| SM-HMM-011 | The model file MUST follow `docs/formats/hmm-model-v1.md`: the writer produces the documented example byte for byte, and the reader rejects anything that deviates from the format. |
| SM-HMM-012 | The model MUST give each step's log-likelihood conditioned on everything observed before it (the forward algorithm's scaling factors, which sum to the sequence's log-likelihood) and the hidden-state distribution after a sequence. Continuing from that distribution MUST give the same steps as scoring the whole sequence. |

Training is plain maximum-likelihood Baum-Welch, which is what guarantees SM-HMM-002. A state that
expects no visits keeps its previous rows instead of dividing by zero. Smoothing is applied after
training, when a card's profile is built (SM-SCORE-013).

Known answers for SM-HMM-001, -003 and -009 come from `tools/golden/generate_hmm_vectors.py`, which
enumerates every state path in exact fractions instead of running a forward-backward or Viterbi
algorithm, and from the worked example in Wikipedia's "Viterbi algorithm" article.

### 6.1 Sparsity (OPEN-2)

5 states × 512 symbols = 2,560 emission parameters per card, fit from a few hundred transactions.
This is badly underdetermined; the model will train without error and learn largely noise.
Candidate mitigations: stronger Dirichlet smoothing on B; a backoff prior toward a population-level
emission matrix; or a coarser alphabet (e.g. folding `is_online` into the MCC group, giving 256).
**Resolved by measurement (§7.1):** after training, each emission row is mixed with the product of
that state's per-feature distributions (SM-SCORE-013). Uniform smoothing did not help at all.

---

## 7. Scoring (SM-SCORE)

**This section replaces the prototype's scoring logic, which was measured to return `LEGIT` for all
512 possible symbols.** Root cause: a length-1 log-likelihood was z-scored against a distribution
of length-20 log-likelihoods, saturating the sigmoid.

| ID | Requirement |
|---|---|
| SM-SCORE-001 | Scoring MUST evaluate a window of the card's `W` most recent transactions, ending with the transaction under test. Its likelihood MUST be conditioned on the card's journal history before the window (forward filtering), not on the model's initial distribution π. `W` configurable; **default 1** (§7.1). |
| SM-SCORE-002 | Likelihood MUST be length-normalized (mean log-likelihood per symbol) so window length cannot bias the score. |
| SM-SCORE-003 | The baseline distribution MUST be built from windows of the same length `W` used at scoring time, conditioned the same way. Comparing unlike lengths MUST be impossible by construction, not by convention: the profile fixes `W`, and scoring takes no window length. |
| SM-SCORE-004 | **Discrimination.** With default options, over 20 synthetic shoppers (seeds 1–20; 300 journal transactions each, then 300 later habitual purchases and 100 each of random, takeover and subtle anomalies), the mean AUC of `fraud_score` MUST be ≥ 0.80. A sweep of all 512 symbols MUST produce more than one verdict class for every shopper. *This test would have failed the prototype.* |
| SM-SCORE-005 | Cold start (no trained model for the card) MUST return `REVIEW` with confidence ≤ 0.3 and anomaly factor `cold_start_no_history`. |
| SM-SCORE-006 | Verdict thresholds: `fraud_score < 0.3` → `LEGIT`; `< 0.7` → `REVIEW`; otherwise `FRAUD_SUSPECTED`. Thresholds configurable. |
| SM-SCORE-007 | Scoring MUST be deterministic: identical input and identical model yield an identical score. |
| SM-SCORE-008 | `fraud_score` MUST lie in [0,1] and MUST never be NaN or infinity for any input accepted by §2 and §5, including a window the model gives probability zero. |
| SM-SCORE-009 | The response MUST include the Viterbi path over the scored window and the expected-likelihood range: the 5th and 95th percentiles (nearest rank) of the card's baseline. |
| SM-SCORE-010 | p95 scoring latency MUST be under 200 ms for a warm model. |
| SM-SCORE-011 | `fraud_score` = clamp(1 − p / α, 0, 1), where p = (1 + the number of baseline windows at most as likely as the scored window) / (n + 1) for n baseline windows, and α = 0.05 by default. A window more ordinary than the card's least-likely α share of its baseline scores 0. |
| SM-SCORE-012 | **Out-of-sample baseline.** The model MUST be trained on the oldest 1 − h of the card's journal (h = 0.33 by default), and the baseline built only from windows whose newest transaction is in the held-out part, so it measures how surprising unseen transactions were. |
| SM-SCORE-013 | **Emission smoothing (resolves OPEN-2).** After training, each emission row MUST be mixed, with weight λ = 0.3, with the product of that state's four per-feature distributions (amount bin, merchant group, time bucket, online), each floored with weight μ = 0.01 toward uniform. Uniform smoothing remains available. Holding data out requires λ > 0. |
| SM-SCORE-014 | `confidence` = max(0, 1 − 1 / ((n + 1)·α)): the highest `fraud_score` the profile can produce. It is 0 at cold start, so a profile too small to raise an alarm says so. |
| SM-SCORE-015 | Scoring is read-only: it MUST NOT change the profile. The window's earlier transactions come from the journal. |

### 7.1 How the defaults were chosen (OPEN-2, OPEN-3)

`tools/SecretModel.ScoringStudy` builds profiles for synthetic shoppers (`tools/SecretModel.Synthetic`),
scores later habitual purchases and injected anomalies, and reports AUC and how often each verdict
comes out. Full output: `docs/studies/scoring-study.md`. History 300, 10 shoppers per row:

| Design | AUC | Normal REVIEW+ | Normal FRAUD | Anomaly REVIEW+ | Anomaly FRAUD |
|---|---|---|---|---|---|
| Windows conditioned on π (first design), W 5, uniform λ 0.01 | 0.678 | 9.6 % | 6.0 % | 28.9 % | 28.3 % |
| Conditioned on history, W 1, uniform λ 0.01, in-sample baseline | 0.894 | 24.1 % | 20.7 % | 93.6 % | 93.1 % |
| … feature backoff λ 0.3 μ 0.01, in-sample baseline | 0.921 | 22.4 % | 15.8 % | 93.0 % | 91.2 % |
| **… held-out baseline h 0.33 (the defaults)** | **0.891** | **2.6 %** | **0.9 %** | **77.9 %** | **70.9 %** |

What the measurements showed:

- **The first design was wrong.** Scoring each window from π, the initial-state distribution, made AUC
  collapse to about 0.54–0.68 as soon as any smoothing was used. Training on one long history fits π
  to its first transaction only. Conditioning each window on the history before it fixed this.
- **An in-sample baseline floods alerts.** Built from the training data itself, the baseline is
  optimistic, and 16–21 % of normal purchases came out FRAUD. Holding out the newest third brought
  that to 0.9 %, close to what α = 0.05 intends, at a small cost in AUC.
- **W = 1 is best (OPEN-3).** With a held-out baseline, longer windows dilute the new transaction's
  surprise with history the model has already seen. AUC by W: 0.891 (1), 0.878 (2), 0.842 (3),
  0.785 (5), 0.826 (10), 0.814 (20).
- **Uniform smoothing makes no difference; feature backoff helps (OPEN-2).** An unseen combination of
  habitual features (usual merchant, amount and time) stays plausible, while unusual features don't.
- **By anomaly kind** (defaults): random 0.921, takeover 0.996, subtle 0.758. A subtle anomaly is a
  habitual purchase with one feature changed and is sometimes still habitual, so it is the hardest.
- **History length limits what a card can say.** With these defaults a card needs at least 85 journal
  transactions before any score can reach `REVIEW`, and at least 200 before `FRAUD_SUSPECTED` is
  possible; `confidence` (SM-SCORE-014) is exactly that ceiling. AUC by history: 0.500 (30), 0.500 (60),
  0.811 (100), 0.891 (300), 0.938 (1,000).
- **Read-only scoring costs nothing here.** Scoring each purchase against its true predecessors
  instead of the journal's tail gave AUC 0.890 vs 0.891 (SM-SCORE-015).
- **Shoppers vary.** Across seeds 1–20 the mean AUC is 0.887; the weakest shopper scored 0.779.

These are synthetic shoppers, not real fraud. The numbers compare designs; they don't predict
performance on real cards.

---

## 8. Journal ingestion (SM-JRNL)

Written against an `IJournalSource` abstraction. **Home mode:** a local folder; files can be placed
there by any means (SFTP through Windows OpenSSH Server is optional and outside this repository).
**Azure mode:** the `journal/` container behind the storage account's SFTP endpoint.

| ID | Requirement |
|---|---|
| SM-JRNL-001 | Journal files are newline-delimited JSON, one tokenized transaction per line, UTF-8, in the locked format `secretmodel-journal` version 1 (`docs/formats/journal-v1.md`). |
| SM-JRNL-002 | A malformed record MUST be quarantined with its line number and reason; the remainder of the batch MUST continue processing. |
| SM-JRNL-003 | Ingestion MUST be idempotent. Reprocessing an already-processed file MUST NOT alter model state. |
| SM-JRNL-004 | Records MUST be grouped by `(client_id, customer_token, card_token)` and ordered by sequence before training. |
| SM-JRNL-005 | A file containing any value that fails SM-PRIV-002 MUST be rejected in full and quarantined. No partial ingestion of a PII-bearing file. |
| SM-JRNL-006 | Successfully processed files MUST move to an archive prefix. A partially failed file MUST remain in place for retry. |
| SM-JRNL-007 | A card with fewer than 10 transactions MUST NOT be trained; it remains cold-start. |
| SM-JRNL-008 | Ingestion MUST record a per-file audit entry: filename, record count, quarantine count, cards trained, timestamp. No transaction content. |

### 8.1 Journal file

- **Format:** `docs/formats/journal-v1.md`, approved by Edwin on 2026-11-26. Each line is one JSON
  object with exactly eight properties, in any order: `seq` (0 to 999,999,999,999), the
  transaction, customer and card tokens, `amount_bin`, `merchant_category` (one of the quantizer's
  eight names, exactly), `hour` and `is_online`. There is no `country_code`, because the model
  doesn't use it. A file names no client: the client comes from the folder the file is in, never
  from its contents (SM-AUTH-003).
- **Reader:** `SecretModel.Core.Journal.JournalFile`. It reads a whole file and returns either the
  valid records and the quarantined lines (line number and reason only, SM-JRNL-002), or a
  rejection of the whole file with the number of the first line that may carry a card number
  (SM-JRNL-005). It never returns anything else from a line. Strings follow JSON; .NET's parser
  turned out to accept a repeated property name, invalid UTF-8 inside a string and an escape such as
  `\ud800` (measured), so the reader checks for each of them itself.
- **Possible card number (SM-PRIV-002 applied to a file):** in a line that is valid JSON, each
  property name and each value is scanned on its own, with an array or object counted as one value
  and well-formed tokens exempt. In any other line the whole text is scanned, both as written and
  with JSON's escapes decoded, and nothing is exempt.
  So a malformed line that holds a well-formed token rejects the file if the token happens to have
  13 decimal digits in a row: for a random token that chance is 1.138%, and 3.375% for a line with
  three tokens (computed exactly, and matched by a simulation of 1,000,000 tokens: 1.150%).
- **Ingestion** (grouping, ordering, training, idempotency, archiving and the audit entry) is §8.2.

**Tests:** `JournalFileTests`, including the format's byte-for-byte known answer.

### 8.2 Home-mode ingestion

Folders, names and rules approved by Edwin on 2026-11-26 and 2026-11-27. `SecretModel.Core.Journal.JournalIngestor`
works against `IJournalSource`; in Home mode that is `FolderJournalSource`.

- **Folders:** each client has its own journal folder, `{journal root}/{client id}/`, and places its
  files there by any means. The server adds `archive/` and `quarantine/` inside it when it first
  needs them. The histories, the profiles and the audit log stay under the model root (§9), which
  only the server uses.
- **Client id:** the folder's name, 1 to 64 ASCII letters, digits, `.`, `_` or `-`, starting with a
  letter or digit, with no run of 13 digits (SM-PRIV-002). Task 9 will hold certificate CNs to the
  same rule (SM-AUTH-003). A folder with any other name is not a client, and its files are left alone.
- **Which files:** only files directly in a client folder whose names end in exactly `.jsonl`. A
  client uploads under another name and renames the file when it is complete. A symbolic link is
  never read, even one named `*.jsonl`, because it could point at another client's file
  (SM-AUTH-006); it is left where it is.
- **File name:** 1 to 100 characters by the client-id rule, then `.jsonl`, with no run of 13 digits
  (SM-PRIV-002, which ignores the dots, dashes and underscores between digits). A file with any other
  name moves to `quarantine/`, unread, renamed as its SHA-256 followed by `.jsonl`, and the audit log
  records no name.
- **Order:** one run handles every waiting file once, by client folder and then by file name
  (ordinal). Only one run may go at a time: the ingestor takes no lock of its own, so the server that
  runs it (Task 9) must not start a run while another is going.
- **Rejected file** (SM-JRNL-005): it moves to `quarantine/` unchanged and nothing from it is used.
  **It then holds what may be a card number.** The server never reads it again; it must be deleted by
  hand.
- **Grouping and order** (SM-JRNL-004): a file's records are grouped by (client, customer token, card
  token) and merged with each card's stored history (§9.3) in `seq` order.
- **Idempotency** (SM-JRNL-003): a `seq` the card already has, with the same symbol, counts once, so a
  file sent again changes nothing. The same `seq` with a different symbol quarantines those lines as
  `SeqConflict`, and what is stored stays. When lines of one file give a new `seq` different symbols,
  all of them are `SeqConflict`, because nothing says which one is right.
- **Training** (SM-JRNL-007): a card with at least 14 transactions in its history (the least the
  default options can build a profile from, `CardProfile.MinimumHistoryFor`) is retrained on its whole
  history, oldest `seq` first, and its profile replaced. With fewer, its history is kept and it stays
  cold-start (SM-SCORE-005), so a card with fewer than 10 is never trained.
- **Archive and retry** (SM-JRNL-006): when every card was saved, the file moves to `archive/`
  unchanged. The archive keeps the whole file, quarantined lines included, so whatever a client puts
  in a line that breaks the format (an unknown property, say) is kept there as written. When a card could not be saved (a damaged stored file, a lost write race, a disk error),
  the file stays where it is and the next run reads it again. What was saved the first time is then
  already stored and is skipped, and a history saved without its profile gets its profile built,
  because the profile's transaction count differs from the history's.
- **Names already taken:** if `archive/` or `quarantine/` already holds a name, the file gets the
  lowest free number before `.jsonl` (`day1.1.jsonl`, `day1.2.jsonl`, …). Nothing is overwritten.
- **Linked folders** (SM-AUTH-006): if a client's `archive/` or `quarantine/` is a link, which could
  point at another client's folder, nothing is moved into it. The run stops with an error that names
  no file, and the file stays where it is. Hard links cannot be told from ordinary files this way;
  keeping clients from making them is left to how the SFTP server is set up.
- **Audit log** (SM-JRNL-008): `{model root}/{client hash}/audit.jsonl`, in the locked format
  `secretmodel-audit` version 1 (`docs/formats/audit-v1.md`). One line is added for every file a run
  handles, a file kept for retry included: the time, the file's name and SHA-256, the outcome
  (`archived`, `retry`, `rejected` or `bad_name`), and the counts of records, new transactions and
  cards trained or failed, with each quarantined line's number and reason. Never transaction content.
  In Azure mode the same bytes are kept in a blob (§9.2).
- **Azure mode:** the same rules, with the journal source of §8.3.
- **Not yet:** running ingestion from the server (Task 9).

**Tests:** `JournalIngestorTests`, `FolderJournalSourceTests`, `JournalNamesTests`, `AuditFileTests`
(including the audit format's byte-for-byte known answer) and `FileAuditLogTests`.

### 8.3 Azure-mode journal source

Design approved by Edwin on 2026-11-27. `SecretModel.Api.Storage.BlobJournalSource`, used by the
same ingestor with the same rules as §8.2. **Not yet tried on real Azure** (Task 8c step 6).

- **Container:** `journal`, in the storage account with the hierarchical namespace and the SFTP
  endpoint (§1), created by the deployment; the server never creates a container.
- **Clients kept apart:** each client has its own folder, `journal/{client id}/`, owned by that
  client's SFTP local user, which has no container permissions and is authorized by ACLs only. A
  client then sees and writes only its own folder: with container permissions it could reach the
  whole container. The deployment sets this up (Task 8c step 6 has the steps).
- **Which files:** each folder at the top of the container is a client's (§8.2 decides whether its
  name is a client id). In it, only blobs directly inside whose names end in exactly `.jsonl`.
  With a hierarchical namespace a directory is listed as a zero-length blob marked with the
  metadata `hdi_isfolder`; such an entry is never read. Files are handled in name order (ordinal):
  a hierarchical namespace lists in depth-first order, so the source sorts them itself.
- **Versions:** each listed file carries its ETag, and reading gets exactly that version. A file
  changed or removed after it was listed is not read: the run stops with an error that names no
  file, and the file is read again on the next run.
- **Moving:** Blob Storage has no rename without a further package. The version that was read is
  written into `archive/` or `quarantine/` only if the name is free (`If-None-Match: *`; otherwise
  the lowest free number, as in §8.2), and the original is then deleted only if it is unchanged
  (`If-Match`). If the client replaced or removed it in between, the copy of what was read stays,
  and a replacement is read on the next run. Nothing is overwritten.
- **Links:** the SFTP endpoint doesn't support symbolic links, so §8.2's link rules have nothing to
  act on in Azure mode.

**Setup and the real-Azure check:** `docs/azure/storage-setup.md`, with
`tools/SecretModel.AzureCheck`. **Not run:** paused by Edwin on 2026-11-27 because of the cost; the
server runs on his PC in Home mode first.

**Tests:** `BlobJournalSourceTests`, against Azurite (§9.2), including four runs of the ingestor
with this source and the Azure-mode stores. Azurite has no hierarchical namespace: directory
entries are imitated there with the metadata, and listing order and SFTP access between clients
are not tested yet.

---

## 9. Persistence (SM-STOR)

Written against an `IModelStore` abstraction (`SecretModel.Core.Storage`). Each card has one
**profile**: its trained model together with its likelihood history, the baseline (SM-SCORE-012).
**Home mode:** a local folder (§9.1). **Azure mode:** Blob Storage (§9.2). Each card also has a
**symbol history**, every transaction ingested for it, kept so it can be retrained (§9.3, behind
`IHistoryStore`).

| ID | Requirement |
|---|---|
| SM-STOR-001 | Models MUST persist at a path derived solely from a `client_id` hash plus card and customer tokens. No PII in the path. |
| SM-STOR-002 | Concurrent writers MUST use optimistic concurrency (Blob ETags in Azure mode; an equivalent version check in Home mode). A losing writer MUST retry or fail loudly; it MUST NOT silently overwrite. |
| SM-STOR-003 | Likelihood history MUST persist alongside the model. **In-process state is prohibited** — the prototype's `history_db` dict is lost on restart and diverges across instances. |
| SM-STOR-004 | In Azure mode, two App Service instances scoring the same transaction MUST return identical results. |
| SM-STOR-005 | A service restart MUST NOT change any score. |
| SM-STOR-006 | `client_id` MUST be part of every storage key so SM-AUTH-006 holds at the storage layer, not just in application logic. |
| SM-STOR-007 | A profile MUST be stored as one file in the locked format `secretmodel-profile` version 1 (`docs/formats/profile-v1.md`), which holds only observation symbols and derived model parameters, counting among those the window length and the number of transactions the profile was built from (SM-PRIV-003). A reader MUST reject a file that does not follow the format exactly. A damaged file MUST be reported, never treated as missing. |
| SM-STOR-008 | A card's symbol history MUST be stored next to its profile, at a path derived like the profile's (SM-STOR-001, SM-STOR-006), as one file in the locked format `secretmodel-history` version 1 (`docs/formats/history-v1.md`), which holds only sequence numbers and observation symbols (SM-PRIV-003). Writes MUST follow SM-STOR-002. A reader MUST reject a file that does not follow the format exactly. A damaged file MUST be reported, never treated as missing. |

### 9.1 Home-mode store

- **Path:** `{root}/{client hash}/{customer token}/{card token}.profile.json`. The client hash is the
  SHA-256 of the `client_id`'s UTF-8 bytes, as 64 lowercase hex characters. Both tokens must pass
  SM-PRIV-001 and be of the right kind (`tok_cus_`, `tok_card_`), so a path can't leave its folder,
  and a rejected token is never repeated in an error.
- **Version:** the SHA-256 of the file's bytes, as lowercase hex. A write names the version it read,
  or none to create, and fails with a conflict when the stored version differs (SM-STOR-002).
  Because the version comes from the content, a file put back byte for byte counts as unchanged.
- **Writing:** a write holds an exclusive lock on `{profile}.lock`, an empty file that stays next to
  the profile. It writes `.{random}.tmp` in the same folder and renames it over the profile, so a
  reader sees the old profile or the new one, never part of one. Windows refuses that rename while a
  reader has the profile open (measured in CI), so on Windows the writer tries again every few
  milliseconds, still holding the lock, and fails loudly after 10 seconds. A failed or interrupted
  write can leave a `.tmp` file behind; it is never read.
- **No cache** (SM-STOR-003): every read opens the file.

**Tests:** `CardProfileFileTests`, `ProfileKeyTests` and `FileModelStoreTests`. The concurrency
tests use several store objects in one process. Writers in separate processes rely on the same
operating-system lock, but that is not tested yet. Azure mode is tested in §9.2.

### 9.2 Azure-mode store

- **Blob name:** the same path as §9.1, `{client hash}/{customer token}/{card token}.profile.json`,
  stored as a block blob with content type `application/json` in one container. The deployment
  creates the container; the server never creates or deletes one. A missing container is an error,
  never "no profile".
- **Version:** the blob's ETag. A create sends `If-None-Match: *`, and a replace sends `If-Match`
  with the version that was read. Blob Storage itself refuses a write that lost a race: 409 when a
  profile already exists, 412 when it was changed or removed (measured on Azurite). The store reports
  either as a conflict (SM-STOR-002). Because Blob Storage does the check, it holds across any number
  of App Service instances (SM-STOR-004).
- **No cache** (SM-STOR-003): every read downloads the blob.
- **Package:** `Azure.Storage.Blobs` 12.29.2. Version 12.30.0 and later depend on Apache.Arrow, a
  third-party package.

**Tests:** `BlobModelStoreTests`, run against **Azurite** 3.38.0, Microsoft's local Blob Storage
emulator. It is pinned with all 369 of its dependencies, each with a SHA-512 hash, in
`tools/azurite/package-lock.json`. Each separate store object stands in for a server instance. CI
installs Azurite with `npm ci --ignore-scripts` and starts it with `--disableTelemetry`; without that
flag, Azurite sends usage data to Microsoft. The tests connect with `UseDevelopmentStorage=true`, the
emulator's public development account, so no key is stored anywhere. If Azurite isn't running, every
Blob store test fails and says how to start it; none is skipped. To run these tests yourself, start
Azurite as the workflow's "Start Azurite" step does, which needs Node.js 22 or newer. Microsoft also
documents Docker, the VS Code extension, and the copy that ships with Visual Studio 2022; none of
those has been tried here. Real Azure Blob Storage is not tested yet.

**History and audit log** (design approved by Edwin on 2026-11-27):
- **History:** one block blob per card, `{client hash}/{customer token}/{card token}.history.json`,
  next to the card's profile in the same container, in the locked format of §9.3
  (`docs/formats/history-v1.md`). Versions, conditional writes and conflicts are exactly as for
  profiles above.
- **Audit log** (SM-JRNL-008): one block blob per client, `{client hash}/audit.jsonl`, in the same
  container, holding exactly the bytes the Home-mode log would (`docs/formats/audit-v1.md`), with
  content type `application/x-ndjson`. Adding a line reads the blob, adds the line and writes it back
  only if the blob is unchanged since the read (`If-Match` its ETag; `If-None-Match: *` for a
  client's first line). An append that lost a race fails loudly with a conflict and drops no other
  writer's line. Only one ingestion run may go at a time (§8.2), so a conflict means something else
  wrote to the log.

**Tests:** `BlobHistoryStoreTests` and `BlobAuditLogTests`, against Azurite as above.
`BlobAuditLogTests` also ingests the same journal into the Home-mode stores and into the Azure-mode
stores and checks that every stored byte is the same.

### 9.3 Symbol history (Home mode)

- **What:** every transaction ingested for a card, as its journal `seq` and its observation symbol,
  in ascending `seq` order (`docs/formats/history-v1.md`, approved by Edwin on 2026-11-26). A profile
  keeps only what scoring needs, so this is what lets a card be retrained on its full history when a
  later journal file brings more transactions. A card with too short a history to train still has
  one, and stays cold-start.
- **Path:** next to the profile, `{root}/{client hash}/{customer token}/{card token}.history.json`,
  under the same root as the profiles, with the same key rules (§9.1).
- **Version, locking and writing:** exactly as for profiles (§9.1), with its own
  `{history}.lock` file. Both stores share one implementation (`VersionedFile`).
- **No cache** (SM-STOR-003): every read opens the file.
- **Azure mode:** §9.2.

**Tests:** `CardHistoryFileTests` (including the format's byte-for-byte known answer) and
`FileHistoryStoreTests`.

---

## 10. Operations (SM-OPS)

| ID | Requirement |
|---|---|
| SM-OPS-001 | `/health` MUST be unauthenticated and MUST return no tenant, model, or configuration data. |
| SM-OPS-002 | No secret, connection string, thumbprint, private key or certificate file may appear in source or in the repository. Secrets come from App Settings or Key Vault (Azure mode) or environment variables or a git-ignored local file (Home mode). |
| SM-OPS-003 | The vault secret used for tokenization MUST NOT be deployed to the server under any configuration. |

---

## 11. PowerShell test harness (SM-PS)

| ID | Requirement |
|---|---|
| SM-PS-001 | Generate a synthetic tokenized journal for N cards with configurable habitual patterns and injected anomalies. |
| SM-PS-002 | Place the journal in the landing zone and wait for ingestion to complete. |
| SM-PS-003 | Score a set of transactions using a client certificate and assert expected verdicts. |
| SM-PS-004 | Negative suite: no cert → 401; unlisted cert → 403; forged `X-ARR-ClientCert` → 403 (Azure) or 401 (Home); raw PAN → 400; `tok_`-prefixed PAN → 400; a cross-tenant card token gets cold-start `REVIEW` (200), the same answer as a card this client has never trained, never the other client's model. |
| SM-PS-005 | Emit a pass/fail summary and exit non-zero on any failed assertion, so CI can consume it. |
| SM-PS-006 | Scripts MUST be runnable against a Home-mode instance as well as a deployed App Service. |

---

## 12. Verification policy (SM-VER)

| ID | Requirement |
|---|---|
| SM-VER-001 | Every requirement in §2–§11 MUST map to at least one automated test, tracked in a coverage matrix. |
| SM-VER-002 | Tests MUST exercise behavior. A test whose only assertion is the presence of a substring in source code does not satisfy SM-VER-001. SM-PRIV-006 is the one rule about source text itself, and its test splits identifiers rather than matching substrings. |
| SM-VER-003 | Every test MUST be demonstrated to fail when the behavior it covers is deliberately broken. An assertion that cannot fail is not a test. |
| SM-VER-004 | CI MUST fail the build on any failing test. |
| SM-VER-005 | No requirement may be marked satisfied before its test has been observed passing **and** observed failing per SM-VER-003. |

---

## 13. Open decisions

| ID | Decision needed | Blocks | Status |
|---|---|---|---|
| OPEN-1 | Journal landing in Azure | §8 | **Resolved 2026-11-25:** Blob Storage SFTP endpoint |
| OPEN-2 | Emission sparsity mitigation | §7 tuning | **Resolved 2026-11-25 by measurement:** feature backoff, SM-SCORE-013 (§7.1) |
| OPEN-3 | Scoring window size `W` | §7 | **Resolved 2026-11-25 by measurement:** W = 1 (§7.1) |
| OPEN-4 | Repository | project setup | **Resolved 2026-11-25:** new repo `edwest19/SecretModel` |
| OPEN-5 | Does the App Service front end overwrite a client-supplied `X-ARR-ClientCert`? Not verified. SM-AUTH-011 tests it against a deployed instance. | Azure deployment | Open |
| OPEN-6 | License for distribution on GitHub | public release | Open: Edwin's decision |
| OPEN-7 | How the client turns an amount into `amount_bin`. The prototype uses KMeans on log amounts, whose bin numbers are not ordered by size, and its fallback for fewer than 8 amounts was measured to put the largest amount (and anything above it) in bin 0, the same bin as the smallest. | client tooling, §11 harness | Open |

---

## 14. Traceability to prototype findings

| Prototype defect (measured) | Addressed by |
|---|---|
| All 512 symbols scored `LEGIT` — length-1 log-likelihood vs length-20 baseline | SM-SCORE-001/002/003/004 |
| `tok_4111111111111111` accepted; the PAN branch was unreachable | SM-PRIV-001, SM-PRIV-002 |
| mTLS was header-string trust (`X-SSL-Client-Verify: SUCCESS`) | SM-AUTH-002, SM-AUTH-004, SM-AUTH-008–011 |
| Models in `/tmp`, history in a process dict | SM-STOR-001, SM-STOR-003, SM-STOR-004 |
| Tests were substring greps | SM-VER-002, SM-VER-003 |
| No journal ingestion existed | §8 |
| No PowerShell harness existed | §11 |
| 2,560 emission parameters per card from a few hundred observations | OPEN-2 |
| Any hour accepted (−1 → bucket 0, 25 → bucket 3) | SM-QNT-003 |
| Fallback amount binning puts the largest amount in bin 0 | OPEN-7 |

---

## 15. Revision history

**v0.16 (2026-11-27)**
- Added §3.2, the scoring endpoint `POST /v1/score`, and its answer format
  `docs/formats/score-response-v1.md`, locked with a known answer. Design approved by Edwin.
- §3.1: the `ModelFolder` setting; any exception is 500 with no detail; the log guard; a refused
  certificate's log line names only the first 12 characters of its thumbprint. Approved by Edwin.
- SM-PS-004's negative cases are tested against the server (the PowerShell harness is still to
  come). SM-AUTH-006, SM-SCORE-010, SM-PRIV-004 and SM-PRIV-005 are now also tested through the
  server, over real TLS.

**v0.15 (2026-11-27)**
- Added §3.1, the Home-mode server: its settings, the test certificates
  (`tools/SecretModel.TestCerts`), the certificate checks and the answers. Design approved by Edwin.
- SM-AUTH-008 now says Kestrel asks for a client certificate and allows a connection without one, so
  `/health` works and other requests get 401. Approved by Edwin.
- SM-PS-004: a cross-tenant card token gets cold-start `REVIEW` (200), never the other client's
  model. Approved by Edwin.
- SM-AUTH-001, -002, -005, -008, -009, -010, SM-OPS-001 and -002 are tested.

**v0.14 (2026-11-27)**
- Added the Azure storage setup steps (`docs/azure/storage-setup.md`) and the check program
  `tools/SecretModel.AzureCheck`. Not run: Edwin paused the real-Azure check because of the cost,
  and the server is run on his PC in Home mode first.

**v0.13 (2026-11-27)**
- Added §8.3, the Azure-mode journal source: one `journal` container, a folder per client owned by
  that client's SFTP local user, versions by ETag, and moves as a copy written only if its name is
  free followed by a delete only if unchanged. Design approved by Edwin.

**v0.12 (2026-11-27)**
- §9.2: the Azure-mode history store and audit log, design approved by Edwin.

**v0.11 (2026-11-27)**
- Added §8.2, Home-mode journal ingestion: the client folders, the name rules, grouping and merging
  by `seq`, `SeqConflict`, training from 14 transactions, archive, quarantine and retry.
- Added the audit log, format `secretmodel-audit` version 1 (`docs/formats/audit-v1.md`).
- SM-PRIV-003 now allows what the audit log holds: file names that follow §8.2, file hashes, times,
  counts, line numbers and reasons. Approved by Edwin.
- A client id may not hold a run of 13 digits, symbolic links in a client folder are never read,
  and nothing is moved into an `archive/` or `quarantine/` that is a link. Approved by Edwin.
- SM-JRNL-003, -004, -006, -007 and -008 are tested.

**v0.10 (2026-11-26)**
- Added §9.3 and SM-STOR-008, each card's symbol history: format `secretmodel-history` version 1
  (`docs/formats/history-v1.md`) and the Home-mode history store.
- SM-PRIV-003 now allows sequence numbers (journal `seq`) to be stored, approved by Edwin, so a
  later journal file can be merged in order and a repeated transaction recognized.

**v0.9 (2026-11-26)**
- Added §8.1, the journal file: format `secretmodel-journal` version 1 (`docs/formats/journal-v1.md`)
  and its strict reader.
- SM-JRNL-001 now names the locked format. SM-JRNL-001, -002 and -005 are tested.

**v0.8 (2026-11-25)**
- Added §9.2, the Azure-mode store: blob names, ETag versions with conditional writes, no cache.
- SM-STOR-002 and SM-STOR-004 are now tested in Azure mode, against Azurite in CI.

**v0.7 (2026-11-25)**
- Added SM-STOR-007: the profile file, format `secretmodel-profile` version 1
  (`docs/formats/profile-v1.md`), with the model and its likelihood history in one file.
- Added §9.1, the Home-mode store: the path, content-hash versions, the lock file, and writing by
  rename.

**v0.6 (2026-11-25)**
- §7 scoring, settled by measurement (§7.1). OPEN-2 resolved (feature backoff), OPEN-3 resolved (W = 1).
- SM-SCORE-001: windows are conditioned on the history before them, not on π (the first design, scored
  from π, measured AUC 0.54–0.68 with smoothing). SM-SCORE-003: the profile fixes W.
- SM-SCORE-004 now averages 20 synthetic shoppers rather than one; the weakest scored 0.779.
- SM-SCORE-008 covers probability-zero windows; SM-SCORE-009 defines the expected range.
- Added SM-SCORE-011 (score from the baseline tail), -012 (held-out baseline), -013 (smoothing),
  -014 (confidence), -015 (read-only scoring) and SM-HMM-012 (conditional step likelihoods).

**v0.5 (2026-11-25)**
- SM-HMM-002 states its rounding tolerance; SM-HMM-004 names SplitMix64; SM-HMM-005 points to the
  locked format.
- Added SM-HMM-009 (exact Baum-Welch update), SM-HMM-010 (input validation, −∞ not NaN) and
  SM-HMM-011 (model file format v1, `docs/formats/hmm-model-v1.md`).
- Training is maximum-likelihood; smoothing stays with OPEN-2.

**v0.4 (2026-11-25)**
- SM-PRIV-001: the whole value must match; no trailing newline.
- SM-PRIV-002 rewritten. The v0.3 wording ("a token whose body, after removing spaces, hyphens and
  underscores, is 13–19 digits") never defined "body" and missed `tok_card_4111111111111111`,
  dotted, slashed, no-break-spaced, zero-width-spaced and full-width forms. The new rule flags
  everything the old one did, plus those.

**v0.3 (2026-11-25)**
- Added SM-TOK-008: lowercase exactly as Python 3.13's `str.lower()`, from data measured from
  Python. .NET's own lowercasing differs from Python for the final sigma and for `İ`, so two golden
  merchant tokens failed; Edwin chose an exact match over ASCII-only lowercasing.
- SM-QNT-003 now rejects hours outside 0–23.
- Noted in §4 that only card values are normalized and that there is no `txn` tokenizer yet.
- Added OPEN-7 (amount → `amount_bin`), with the measured fallback-binning defect.

**v0.2 (2026-11-25)**
- Spec moved into README.md, which is now the specification.
- OPEN-1 resolved (Blob SFTP endpoint); OPEN-4 resolved (`edwest19/SecretModel`).
- Added Home mode (§1) so the service runs on Edwin's network before Azure: Kestrel requires the
  client certificate at the handshake, with local folders for models and journal.
- Added SM-AUTH-008 to SM-AUTH-011. SM-AUTH-004 now applies to Azure mode only.
- Added OPEN-5: the forged-header case for an *allowlisted* certificate depends on App Service
  behavior that has not been verified.
- SM-PRIV-006 is now enforced by an identifier-splitting test instead of a build-time analyzer. An
  analyzer needs packages that are not on the approved list.
- SM-STOR-002 now covers both stores; SM-STOR-004 applies to Azure mode only.
- SM-OPS-002 and SM-PS-004 now cover both modes.
- Added OPEN-6 (license).

**v0.1 (2026-09-13)**: first draft, written outside the repository.
