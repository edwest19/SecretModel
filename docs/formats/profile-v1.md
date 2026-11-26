<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated. -->

# Card profile file, format `secretmodel-profile` version 1

The file that stores everything needed to score one card (README §7 and §9): its trained model
together with its likelihood history (SM-STOR-003). Written and read by
`SecretModel.Core.Scoring.CardProfileFile`. This document locks the format; the known-answer test
`CardProfileFileTests.Write_MatchesTheDocumentedBytes` checks the example below byte for byte.

## Encoding

- UTF-8 JSON, no byte-order mark, a single line, no trailing newline.
- Exactly one JSON object with exactly these nine properties, in this order:

| Property | JSON type | Meaning |
|---|---|---|
| `format` | string | Always `"secretmodel-profile"`. |
| `version` | number | Always `1`. |
| `window` | number | W, the window length the profile was built for, a whole number ≥ 1. |
| `history` | number | How many journal transactions it was built from, ≥ `window`. |
| `recent` | array of W − 1 whole numbers | The last W − 1 history symbols, oldest first, each 0 to M − 1. |
| `state_before` | array of N numbers | The hidden-state distribution just before `recent`; sums to 1 within 1e-9. |
| `seen` | array of 1 to `history` whole numbers | Every symbol that occurs in the history, ascending, no repeats, each 0 to M − 1. Includes every `recent` symbol. |
| `baseline` | array of numbers | The baseline (README SM-SCORE-012), ascending, finite, 1 to `history − window + 1` values. |
| `model` | object | The card's HMM, exactly as a `secretmodel-hmm` version 1 file (`docs/formats/hmm-model-v1.md`). N and M come from it. |

- Numbers are written in the shortest decimal form that reads back as the same IEEE-754 double, so
  a profile round-trips bit for bit. A reader must accept any JSON number syntax.

The file holds only observation symbols and derived model parameters, counting among those the
window length and the history count (README SM-PRIV-003). It holds no tokens, no client
identifier, no amounts and no text: its only strings are the two format names. Where it is stored
is README §9.

## Reading

A reader rejects the file (in C#, `InvalidDataException`) when it is not valid JSON; when the root is
not an object; when any property is missing, extra, or out of order; when `format` or `version`
differs; when `model` is not a valid `secretmodel-hmm` version 1 object; or when any rule above
fails.

## Example (known answer)

A profile with W = 2, built from 12 transactions, whose model is the example in
`docs/formats/hmm-model-v1.md`:

| | |
|---|---|
| `recent` | 2 |
| `state_before` | 0.25, 0.75 |
| `seen` | 0, 1, 2 |
| `baseline` | −1.5, −0.75, −0.5 |

is written as exactly these 295 bytes:

```
{"format":"secretmodel-profile","version":1,"window":2,"history":12,"recent":[2],"state_before":[0.25,0.75],"seen":[0,1,2],"baseline":[-1.5,-0.75,-0.5],"model":{"format":"secretmodel-hmm","version":1,"states":2,"symbols":3,"pi":[1,0],"a":[[0.75,0.25],[0,1]],"b":[[0.5,0.25,0.25],[0.1,0.2,0.7]]}}
```
