# Author: Meta AI in consultation with edwest19 - SecretModel

from fastapi import Header, HTTPException, Depends

async def require_mtls_client(
    x_ssl_client_verify: str = Header(None, alias="X-SSL-Client-Verify"),
    x_ssl_client_cn: str = Header(None, alias="X-SSL-Client-CN"),
    x_ssl_client_dn: str = Header(None, alias="X-SSL-Client-DN")
):
    if x_ssl_client_verify != "SUCCESS":
        raise HTTPException(status_code=401, detail="mTLS client certificate required. Server only accepts tokenized data from verified clients.")
    if not x_ssl_client_cn:
        raise HTTPException(status_code=401, detail="Client CN missing from mTLS cert")
    return {"client_id": x_ssl_client_cn, "client_dn": x_ssl_client_dn}

def require_token_format(token: str):
    if not token.startswith("tok_"):
        raise HTTPException(status_code=400, detail=f"Token not tokenized. Server only accepts tok_* tokens. Raw PII rejected.")
    if len(token) < 20:
        raise HTTPException(400, detail="Invalid token format")
    stripped = token.replace(" ", "").replace("-", "")
    if stripped.isdigit() and 13 <= len(stripped) <= 19:
        raise HTTPException(400, detail="Raw PAN detected - rejected. Client must tokenize before sending.")
