<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated. -->

# HMM model file, format `secretmodel-hmm` version 1

The file that stores one trained per-card Hidden Markov Model (README §6, SM-HMM-005 and
SM-HMM-011). Written and read by `SecretModel.Core.Hmm.HmmModelFile`. This document locks the
format; the known-answer test `HmmModelFileTests.Write_MatchesTheDocumentedBytes` checks that the
writer produces the example below byte for byte.

## Encoding

- UTF-8 JSON, no byte-order mark, a single line, no trailing newline.
- Exactly one JSON object with exactly these seven properties, in this order:

| Property | JSON type | Meaning |
|---|---|---|
| `format` | string | Always `"secretmodel-hmm"`. |
| `version` | number | Always `1`. |
| `states` | number | Number of hidden states *N*, a whole number ≥ 1. |
| `symbols` | number | Number of observation symbols *M*, a whole number ≥ 1. |
| `pi` | array of *N* numbers | Initial state probabilities. |
| `a` | array of *N* arrays of *N* numbers | `a[i][j]` = P(next state *j* \| state *i*). |
| `b` | array of *N* arrays of *M* numbers | `b[i][k]` = P(symbol *k* \| state *i*). |

- Every probability is finite and ≥ 0, and `pi`, each row of `a` and each row of `b` sums to 1
  within 1e-9.
- Numbers are written in the shortest decimal form that reads back as the same IEEE-754 double, so
  a file round-trips bit for bit. A reader must accept any JSON number syntax.

The file holds only model parameters: no tokens, amounts or other transaction data (README
SM-PRIV-003). Where it is stored, and under what name, is covered by §9 of the README.

## Reading

A reader rejects the file (in C#, `InvalidDataException`) when it is not valid JSON; when the root is
not an object; when any property is missing, extra, or out of order; when `format` or `version`
differs; when `states` or `symbols` is not a whole number ≥ 1; when an array has the wrong length or
holds anything but finite numbers; or when the parameters fail the rules above.

## Example (known answer)

A model with 2 states and 3 symbols:

| | |
|---|---|
| `pi` | 1, 0 |
| `a` | [0.75, 0.25], [0, 1] |
| `b` | [0.5, 0.25, 0.25], [0.1, 0.2, 0.7] |

is written as exactly these 134 bytes:

```
{"format":"secretmodel-hmm","version":1,"states":2,"symbols":3,"pi":[1,0],"a":[[0.75,0.25],[0,1]],"b":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}
```
