# Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
#
# Generates the golden-vector files for README SM-TOK-007 and SM-QNT-006 by running the unmodified
# Python prototype in reference/python. The C# tests compare against these files, so the expected
# values come from the reference code, not from the C# being tested.
#
# Requires Python 3 with numpy and scikit-learn (the prototype's client/quantizer.py imports both).
# Run from the repository root:   python3 -I tools/golden/generate_golden_vectors.py
# Output is deterministic: running it again must produce byte-identical files.

import hashlib
import json
import os
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
REFERENCE = os.path.join(REPO, "reference", "python")
OUT_DIR = os.path.join(REPO, "tests", "SecretModel.Tests", "GoldenVectors")
sys.path.insert(0, REFERENCE)

from client.tokenizer import ClientTokenizer  # noqa: E402  (reference code, unmodified)
from client.quantizer import ClientQuantizer, MCC_GROUPS  # noqa: E402

# A fixed, synthetic test secret. Not a real key; it exists only to make these vectors reproducible.
SECRET = b"SecretModel golden-vector test secret. Not a real key."
assert len(SECRET) >= 32


def sha256_of(relative_path):
    with open(os.path.join(REFERENCE, relative_path), "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def write(name, document):
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, name)
    # ASCII-only JSON (non-ASCII as \u escapes) so line endings and encodings can't alter the bytes.
    text = json.dumps(document, indent=1, sort_keys=True, ensure_ascii=True) + "\n"
    with open(path, "w", encoding="ascii", newline="\n") as f:
        f.write(text)
    print(f"wrote {os.path.relpath(path, REPO)}: {len(document['vectors'])} vectors")


def tokenizer_vectors():
    t = ClientTokenizer(SECRET)
    cases = []

    # Card values: published test card numbers (synthetic), plus the separators the reference strips.
    for value in [
        "4111111111111111",
        "4111 1111 1111 1111",
        "4111-1111-1111-1111",
        "4111 - 1111-1111 1111",
        "5555555555554444",
        "378282246310005",
        "6011111111111117",
        "",
    ]:
        cases.append({"method": "card", "input": value, "token": t.tokenize_card(value)})

    # Customer IDs: used exactly as given (no case folding, no trimming).
    for value in ["cust-0001", "CUST-0001", "cust 0001", " cust-0001", ""]:
        cases.append({"method": "customer", "input": value, "token": t.tokenize_customer(value)})

    # Merchant names: lowercased with Python's str.lower() before hashing. The non-ASCII entries check
    # that C# lowercases the same way Python does.
    for value in [
        "Acme Grocery",
        "ACME GROCERY",
        "acme grocery",
        "Café Ünïcode",          # Café Ünïcode
        "ΟΔΟΣ",              # ΟΔΟΣ (final sigma)
        "İstanbul Bazaar",                  # İstanbul (capital I with dot above)
        "Straße",                           # Straße
        "ẞ",                                # ẞ capital sharp s
        "Kelvin Hardware",                  # Kelvin sign
        "\U0001f6d2 Mart",                       # shopping-cart emoji, outside the BMP
        "",
    ]:
        cases.append({"method": "merchant", "input": value, "token": t.tokenize_merchant_name(value)})

    return {
        "description": "Tokens produced by reference/python/client/tokenizer.py (README SM-TOK-007).",
        "generator": "tools/golden/generate_golden_vectors.py",
        "reference_sha256": {"client/tokenizer.py": sha256_of("client/tokenizer.py")},
        "secret_hex": SECRET.hex(),
        "vectors": cases,
    }


def symbol(amount_bin, category, hour, is_online):
    # The reference turns an amount into a bin with KMeans, which SM-QNT does not cover. Replacing only
    # that one method lets the reference's own code compute the MCC group, time bucket and packing.
    q = ClientQuantizer()
    q.quantize_to_bin = lambda _amount: amount_bin
    return q.quantize_to_symbol(0.0, category, hour, is_online)


def quantizer_vectors():
    names_by_group = sorted(MCC_GROUPS.items(), key=lambda kv: kv[1])
    cases = []

    # All 512 combinations. The hour varies inside each time bucket so every hour 0-23 is used.
    for a in range(8):
        for name, m in names_by_group:
            for t in range(4):
                for o in (False, True):
                    hour = 6 * t + (a + m + int(o)) % 6
                    cases.append({"amount_bin": a, "category": name, "hour": hour, "is_online": o,
                                  "symbol": symbol(a, name, hour, o), "set": "all_512"})

    # Every hour, to pin the time-bucket boundaries.
    for hour in range(24):
        cases.append({"amount_bin": 3, "category": "retail", "hour": hour, "is_online": False,
                      "symbol": symbol(3, "retail", hour, False), "set": "hours"})

    # Category spellings: case folding, unknown names, and a Kelvin sign that lowercases to "k".
    for category in ["Grocery", "GROCERY", "ATM", "Online_Market", "casino", "gas station",
                     " grocery", "", "online_marKet"]:
        cases.append({"amount_bin": 1, "category": category, "hour": 9, "is_online": True,
                      "symbol": symbol(1, category, 9, True), "set": "categories"})

    return {
        "description": "Symbols produced by reference/python/client/quantizer.py (README SM-QNT-006). "
                       "Only quantize_to_bin is replaced, so amount_bin can be chosen directly.",
        "generator": "tools/golden/generate_golden_vectors.py",
        "reference_sha256": {"client/quantizer.py": sha256_of("client/quantizer.py")},
        "vectors": cases,
    }


if __name__ == "__main__":
    write("tokenizer.json", tokenizer_vectors())
    write("quantizer.json", quantizer_vectors())
