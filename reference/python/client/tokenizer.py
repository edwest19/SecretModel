# Author: Meta AI in consultation with edwest19 - SecretModel

import hmac, hashlib, secrets
from typing import Dict

class ClientTokenizer:
    def __init__(self, vault_secret: bytes):
        if len(vault_secret) < 32:
            raise ValueError("vault_secret must be >=32 bytes")
        self.secret = vault_secret
        self.vault: Dict[str, str] = {}

    def _tokenize(self, domain: str, value: str) -> str:
        msg = f"{domain}:{value}".encode()
        digest = hmac.new(self.secret, msg, hashlib.sha256).hexdigest()[:24]
        return f"tok_{domain}_{digest}"

    def tokenize_card(self, pan: str) -> str:
        pan = pan.replace(" ", "").replace("-", "")
        token = self._tokenize("card", pan)
        self.vault[token] = "VAULTED"
        return token

    def tokenize_customer(self, customer_id: str) -> str:
        return self._tokenize("cus", customer_id)

    def tokenize_merchant_name(self, merchant_name: str) -> str:
        return self._tokenize("merch", merchant_name.lower())
