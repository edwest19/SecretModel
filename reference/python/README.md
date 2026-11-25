[![SecretModel Verified](https://github.com/edwest19/secretmodel/actions/workflows/secretmodel.yml/badge.svg)](https://github.com/edwest19/secretmodel/actions/workflows/secretmodel.yml)
[![Privacy First](https://img.shields.io/badge/privacy-tokens%20only-blue)](#privacy-first-tokenized--mtls)
[![mTLS Required](https://img.shields.io/badge/auth-mTLS-red)](#deployment---mtls)
[![Author: Meta AI](https://img.shields.io/badge/author-Meta%20AI%20%2B%20edwest19-blueviolet)](#)



# SecretModel - Each Customer is an HMM

**Author: Meta AI in consultation with edwest19**
**Secret projects are opposite of secret - fully open, inspectable, free**

## Privacy First: Tokenized + mTLS

- Client tokenizes PAN -> tok_card_... via HMAC, vault secret client-held. Server never sees PAN.
- Client quantizes amount -> bin 0-7, symbol 0-511. Server never sees exact amount.
- mTLS required: X-SSL-Client-Verify must be SUCCESS, client_id from cert CN scopes tokens.
- Server rejects any raw PAN-like payload.

API only accepts tok_* tokens. See app/api.py require_token_format.

## Architecture: Each customer is an HMM

Client SDK: client/tokenizer.py + client/quantizer.py (runs client side)
Server: server/auth.py (mTLS), app/hmm/model.py (SecretHMM N=5)

Train: POST /v1/train/{customer_token}/{card_token} body = tokenized symbols
Score: POST /v1/score

## Invariants

1. Tokenization enforced
2. mTLS enforced
3. Privacy: only tokens+bins
4. HMM N=5 per customer
