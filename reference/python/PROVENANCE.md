<!-- Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated. -->

# Provenance: Python prototype

This folder holds the Python prototype that SecretModel's C# version is ported from. It is kept
**unmodified**, byte for byte, as a reference. Every file here except this one carries its
original attribution: *Meta AI in consultation with edwest19*.

Do not edit these files. The C# code fixes the defects listed in README.md §14; the prototype
stays as it was received so those findings can be reproduced against it.

## Source

| | |
|---|---|
| Archive | `SecretModel-Final-Renamed.zip` |
| Archive SHA-256 | `3bf6bd2a819e5fa6b6743aa77095974663833f476c64d63b9ac0fb34bcd4102b` |
| Archive size | 19,229 bytes, 17 files |
| Received | Uploaded by Edwin West to a Claude conversation, 2026-09-13 |
| Extracted | 2026-11-25, `unzip`, top folder `secretmodel/` removed, contents compared with `diff -r`: identical |

## Files

SHA-256, size in bytes, path.

```
5f4cf8930500530c4a0e59a3031b6fc832577d83a8bc3f01a8dd455641d12341     695  .github/workflows/secretmodel.yml
6970083132f40e111eced72c2196c7f7ebb99823a5fd3d7cc34be62d0ac0ffc1    1459  README.md
6970083132f40e111eced72c2196c7f7ebb99823a5fd3d7cc34be62d0ac0ffc1    1459  README_1.md
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855       0  app/__init__.py
900e6b2e61b0f722b845f35763ec1dc44e7f39ccfd1e2cded4da1365e50f3a28    5297  app/api.py
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855       0  app/features/__init__.py
26581ac168a856f9356f34c0f3ed94e0b996e2aeaea52ef7050b2a6fa958a949     252  app/features/quantizer.py
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855       0  app/hmm/__init__.py
b4d71fa8a01a943cad4417ee2a9d8d7d5383f133779792eb0d1ac50d2c1104a7    3042  app/hmm/model.py
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855       0  client/__init__.py
a625a89a8dd725c5b405445be5361bf2d7460f0ffa9ed4eb31b4e9bf71ec60f5    1392  client/quantizer.py
75d99d2a9922f312a01277f29ac1c0512623df92cb216d620bab229874c77d5b    1017  client/tokenizer.py
06e16983723b17b1156585f5c4989f32ddaa58fc127397175895074aa4195691      44  requirements.txt
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855       0  server/__init__.py
c46f4b03371bd8e678c717394cfe7a670b6e267422f428a1dfe31ea1b20a8aa0    1244  server/auth.py
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855       0  tests/__init__.py
c96652300522092179a037badec2232e791036a215b7a4665defecd7ecde7fd2     994  tests/agent_verifier.py
```

To check them on Linux or macOS, run `sha256sum` (or `shasum -a 256`) from this folder. On
Windows PowerShell, run `Get-FileHash -Algorithm SHA256 <file>`.

## Notes

- `README_1.md` is a byte-identical copy of `README.md` (same hash). Both are kept as received.
- `.github/workflows/secretmodel.yml` is the prototype's CI file. GitHub only runs workflows from
  the repository's root `.github/workflows/`, so this copy never runs.
- The five empty `__init__.py` files all have the SHA-256 of zero bytes (`e3b0c442…`).
