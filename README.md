<!-- Written by Claude (Anthropic) for SecretModel. v0.1 drafted 2026-09-13; v0.2 by Claude Opus 5.5, 2026-11-25. AI-generated. -->

# SecretModel

SecretModel is a test and development fraud-detection service. Each payment card is modeled as its
own Hidden Markov Model. A historical journal of tokenized test debit-card transactions arrives by
SFTP and trains the per-card models. Real-time scoring requests are evaluated against those models
over HTTPS with mutual TLS.

**This is AI-generated code.** Claude (Anthropic) writes the code, tests and documentation; Edwin
West decides what it does and approves every merge. It is for demonstration, testing and comparison,
not production, and it never handles real card or cardholder data.

**Status:** specification only. Nothing below is implemented yet. A requirement counts as met only
when its test has been seen passing *and* seen failing (§12).

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
| `src/SecretModel.Core` | Logic: tokenizer, quantizer, token validation, HMM, scoring. No ASP.NET or Azure dependencies. |
| `src/SecretModel.Api` | ASP.NET Core host: endpoints, client-certificate authentication, storage adapters. |
| `tests/SecretModel.Tests` | MSTest tests, one or more per requirement. |
| `reference/python` | The Meta AI Python prototype, unmodified. See its `PROVENANCE.md`. |

---

# Requirements Specification

**Version:** 0.2
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
| SM-PRIV-001 | The server MUST reject any token that does not match `^tok_(cus\|card\|txn\|merch)_[0-9a-f]{24}$`. Prefix-only checking is insufficient. |
| SM-PRIV-002 | The server MUST reject any token whose body, after removing spaces, hyphens and underscores, is 13–19 digits. **This closes the `tok_4111111111111111` bypass confirmed in the prototype.** |
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

---

## 5. Quantization (SM-QNT)

| ID | Requirement |
|---|---|
| SM-QNT-001 | `symbol = ((amount_bin * 8 + mcc_group) * 4 + time_bucket) * 2 + is_online`. |
| SM-QNT-002 | The mapping MUST be a bijection onto exactly 0–511 with zero collisions. *(Verified to hold in the Python reference.)* |
| SM-QNT-003 | Time buckets: hour ∈ [0,6) → 0, [6,12) → 1, [12,18) → 2, [18,24) → 3. |
| SM-QNT-004 | An unrecognized merchant category MUST map to group 7 (`other`), never throw. |
| SM-QNT-005 | `amount_bin` MUST be 0–7 inclusive; out-of-range input is rejected, not clamped silently. |
| SM-QNT-006 | The C# quantizer MUST produce identical symbols to the Python reference across a shared golden-vector file covering all 512 combinations. |

---

## 6. HMM core (SM-HMM)

| ID | Requirement |
|---|---|
| SM-HMM-001 | The forward algorithm MUST use per-timestep scaling. Log-likelihood MUST match a hand-computed value for a 3-state / 3-symbol fixture to within 1e-9. |
| SM-HMM-002 | Baum-Welch log-likelihood MUST be monotonically non-decreasing across iterations on a fixed training set. A decrease indicates an implementation error and MUST fail the build. |
| SM-HMM-003 | Viterbi MUST return the known-optimal state path for a published textbook fixture. |
| SM-HMM-004 | Training MUST be deterministic given a fixed seed: two runs produce bit-identical parameters. |
| SM-HMM-005 | Serialize → deserialize MUST preserve A, B, Pi exactly (bitwise for IEEE-754 doubles). |
| SM-HMM-006 | After every training iteration, rows of A, rows of B, and Pi MUST each sum to 1.0 within 1e-9. |
| SM-HMM-007 | A 10,000-symbol sequence MUST score without underflow, overflow, NaN, or infinity. |
| SM-HMM-008 | `n_states = 5`, `n_symbols = 512` by default, both configurable. |

### 6.1 Sparsity (OPEN-2)

5 states × 512 symbols = 2,560 emission parameters per card, fit from a few hundred transactions.
This is badly underdetermined; the model will train without error and learn largely noise.
Candidate mitigations: stronger Dirichlet smoothing on B; a backoff prior toward a population-level
emission matrix; or a coarser alphabet (e.g. folding `is_online` into the MCC group, giving 256).
To be decided with measurements from SM-SCORE-004, not by assertion.

---

## 7. Scoring (SM-SCORE)

**This section replaces the prototype's scoring logic, which was measured to return `LEGIT` for all
512 possible symbols.** Root cause: a length-1 log-likelihood was z-scored against a distribution
of length-20 log-likelihoods, saturating the sigmoid.

| ID | Requirement |
|---|---|
| SM-SCORE-001 | Scoring MUST evaluate a window of the card's `W` most recent transactions, ending with the transaction under test. `W` configurable. |
| SM-SCORE-002 | Likelihood MUST be length-normalized (mean log-likelihood per symbol) so window length cannot bias the score. |
| SM-SCORE-003 | The baseline distribution MUST be built from windows of the same length `W` used at scoring time. Comparing unlike lengths MUST be impossible by construction, not by convention. |
| SM-SCORE-004 | **Discrimination.** On a synthetic cardholder with injected anomalies, the scorer MUST separate anomalous from habitual transactions with AUC ≥ 0.80. A sweep of all 512 symbols MUST produce more than one verdict class. *This test would have failed the prototype.* |
| SM-SCORE-005 | Cold start (no trained model for the card) MUST return `REVIEW` with confidence ≤ 0.3 and anomaly factor `cold_start_no_history`. |
| SM-SCORE-006 | Verdict thresholds: `fraud_score < 0.3` → `LEGIT`; `< 0.7` → `REVIEW`; otherwise `FRAUD_SUSPECTED`. Thresholds configurable. |
| SM-SCORE-007 | Scoring MUST be deterministic: identical input and identical model yield an identical score. |
| SM-SCORE-008 | `fraud_score` MUST lie in [0,1] and MUST never be NaN or infinity for any input accepted by §2 and §5. |
| SM-SCORE-009 | The response MUST include the Viterbi path over the scored window and the expected-likelihood range. |
| SM-SCORE-010 | p95 scoring latency MUST be under 200 ms for a warm model. |

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

Written against an `IModelStore` abstraction. **Home mode:** a local folder. **Azure mode:** Blob
Storage.

| ID | Requirement |
|---|---|
| SM-STOR-001 | Models MUST persist at a path derived solely from a `client_id` hash plus card and customer tokens. No PII in the path. |
| SM-STOR-002 | Concurrent writers MUST use optimistic concurrency (Blob ETags in Azure mode; an equivalent version check in Home mode). A losing writer MUST retry or fail loudly; it MUST NOT silently overwrite. |
| SM-STOR-003 | Likelihood history MUST persist alongside the model. **In-process state is prohibited** — the prototype's `history_db` dict is lost on restart and diverges across instances. |
| SM-STOR-004 | In Azure mode, two App Service instances scoring the same transaction MUST return identical results. |
| SM-STOR-005 | A service restart MUST NOT change any score. |
| SM-STOR-006 | `client_id` MUST be part of every storage key so SM-AUTH-006 holds at the storage layer, not just in application logic. |

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
| OPEN-2 | Emission sparsity mitigation | §7 tuning | Open: decide by measurement |
| OPEN-3 | Scoring window size `W` | §7 | Open: decide by measurement against SM-SCORE-004 |
| OPEN-4 | Repository | project setup | **Resolved 2026-11-25:** new repo `edwest19/SecretModel` |
| OPEN-5 | Does the App Service front end overwrite a client-supplied `X-ARR-ClientCert`? Not verified. SM-AUTH-011 tests it against a deployed instance. | Azure deployment | Open |
| OPEN-6 | License for distribution on GitHub | public release | Open: Edwin's decision |

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

---

## 15. Revision history

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
