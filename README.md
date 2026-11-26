<!-- Written by Claude (Anthropic) for SecretModel. v0.1 drafted 2026-09-13; v0.2 to v0.7 by Claude Opus 5.5, 2026-11-25. AI-generated. -->

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
(§7), and the profile file and Home-mode model store (§9). A requirement counts as met only when its test has been seen passing *and* seen failing (§12).

**This README is the specification.** It changes in the same pull request as the behavior it
describes.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build SecretModel.slnx -c Release
```

CI (`.github/workflows/build.yml`) builds and tests every pull request and every push to `main` on
ubuntu-latest and windows-latest. A failing test or a compiler warning fails the run.

## Repository layout

| Path | What it holds |
|---|---|
| `src/SecretModel.Core` | Logic: tokenizer, quantizer, token validation, HMM, scoring, the profile file and the Home-mode model store. No ASP.NET or Azure dependencies. |
| `src/SecretModel.Api` | ASP.NET Core host: endpoints, client-certificate authentication, the Azure Blob model store. |
| `tests/SecretModel.Tests` | MSTest tests, one or more per requirement. |
| `reference/python` | The Meta AI Python prototype, unmodified. See its `PROVENANCE.md`. |
| `docs/formats` | Locked file formats, each with a known-answer test. |
| `tools/golden` | Produces the tests' golden vectors: by running the Python reference, or by exhaustive exact computation. |
| `tools/unicode` | Measures Python's lowercasing to produce `PythonLowercaseData.g.cs` (SM-TOK-008). |
| `tools/SecretModel.Synthetic` | Synthetic shoppers and injected anomalies, for tests, the study and the harness. |
| `tools/SecretModel.ScoringStudy` | The measurement behind §7.1. |
| `docs/studies` | Full output of each measurement that decided a setting. |

---

# Requirements Specification

**Version:** 0.7
**Date:** 2026-11-25
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
| SM-PRIV-003 | The server MUST NOT persist any value other than: tokens, amount bins, observation symbols, timestamp buckets, country codes, merchant category codes, and derived model parameters. |
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
| SM-AUTH-008 | In Home mode, Kestrel MUST require a client certificate during the TLS handshake, and the certificate MUST pass SM-AUTH-002. |
| SM-AUTH-009 | In Home mode, the service MUST ignore `X-ARR-ClientCert` entirely. A request whose only certificate is in that header MUST be rejected exactly as if it carried no certificate. |
| SM-AUTH-010 | The run mode MUST come from configuration only, never from a request. An absent or unrecognized mode MUST start in Home mode, which never trusts the header. |
| SM-AUTH-011 | In Azure mode, a request that presents certificate X in the TLS handshake and an `X-ARR-ClientCert` header for a different, allowlisted certificate Y MUST be rejected or authenticated as X, never as Y. Verified against a deployed App Service. See OPEN-5. |

**Tests:** integration tests with real self-signed certificate pairs; a forged-header test that
sends a valid-looking public certificate with no proof of possession and asserts 403; a
cross-tenant test where two clients train the same card token and each sees only its own model; a
Home-mode test proving the header is ignored.

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
| SM-JRNL-001 | Journal files are newline-delimited JSON, one tokenized transaction per line, UTF-8. |
| SM-JRNL-002 | A malformed record MUST be quarantined with its line number and reason; the remainder of the batch MUST continue processing. |
| SM-JRNL-003 | Ingestion MUST be idempotent. Reprocessing an already-processed file MUST NOT alter model state. |
| SM-JRNL-004 | Records MUST be grouped by `(client_id, customer_token, card_token)` and ordered by sequence before training. |
| SM-JRNL-005 | A file containing any value that fails SM-PRIV-002 MUST be rejected in full and quarantined. No partial ingestion of a PII-bearing file. |
| SM-JRNL-006 | Successfully processed files MUST move to an archive prefix. A partially failed file MUST remain in place for retry. |
| SM-JRNL-007 | A card with fewer than 10 transactions MUST NOT be trained; it remains cold-start. |
| SM-JRNL-008 | Ingestion MUST record a per-file audit entry: filename, record count, quarantine count, cards trained, timestamp. No transaction content. |

---

## 9. Persistence (SM-STOR)

Written against an `IModelStore` abstraction (`SecretModel.Core.Storage`). Each card has one
**profile**: its trained model together with its likelihood history, the baseline (SM-SCORE-012).
**Home mode:** a local folder (§9.1). **Azure mode:** Blob Storage.

| ID | Requirement |
|---|---|
| SM-STOR-001 | Models MUST persist at a path derived solely from a `client_id` hash plus card and customer tokens. No PII in the path. |
| SM-STOR-002 | Concurrent writers MUST use optimistic concurrency (Blob ETags in Azure mode; an equivalent version check in Home mode). A losing writer MUST retry or fail loudly; it MUST NOT silently overwrite. |
| SM-STOR-003 | Likelihood history MUST persist alongside the model. **In-process state is prohibited** — the prototype's `history_db` dict is lost on restart and diverges across instances. |
| SM-STOR-004 | In Azure mode, two App Service instances scoring the same transaction MUST return identical results. |
| SM-STOR-005 | A service restart MUST NOT change any score. |
| SM-STOR-006 | `client_id` MUST be part of every storage key so SM-AUTH-006 holds at the storage layer, not just in application logic. |
| SM-STOR-007 | A profile MUST be stored as one file in the locked format `secretmodel-profile` version 1 (`docs/formats/profile-v1.md`), which holds only observation symbols and derived model parameters, counting among those the window length and the number of transactions the profile was built from (SM-PRIV-003). A reader MUST reject a file that does not follow the format exactly. A damaged file MUST be reported, never treated as missing. |

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
operating-system lock, but that is not tested yet. SM-STOR-004, and SM-STOR-002 in Azure mode, come
with the Blob store.

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
| SM-PS-004 | Negative suite: no cert → 401; unlisted cert → 403; forged `X-ARR-ClientCert` → 403 (Azure) or 401 (Home); raw PAN → 400; `tok_`-prefixed PAN → 400; cross-tenant card token → 404 or 403. |
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
