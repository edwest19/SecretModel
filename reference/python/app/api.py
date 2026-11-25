# Author: Meta AI in consultation with edwest19 - SecretModel

from fastapi import FastAPI, HTTPException, Depends
from pydantic import BaseModel, Field
from typing import Literal, List
import numpy as np
import os, re
from .hmm.model import SecretHMM
from .features.quantizer import ServerQuantizer
from ..server.auth import require_mtls_client, require_token_format

app=FastAPI(title="SecretModel - Each customer is an HMM", version="0.2.0",
            description="Privacy-first fraud detection. Server only sees tokens, never PII. mTLS required.")

class TokenizedTransaction(BaseModel):
    transaction_id_token: str = Field(..., description="tok_txn_... client tokenized")
    customer_token: str = Field(..., description="tok_cus_... client tokenized customer_id")
    card_token: str = Field(..., description="tok_card_... HMAC of PAN")
    observation_symbol: int = Field(..., ge=0, lt=512)
    amount_bin: int = Field(..., ge=0, lt=8)
    merchant_category: str
    country_code: str
    is_online: bool
    timestamp_bucket: int = Field(..., ge=0, lt=4)

class TrainRequest(BaseModel):
    transactions: List[TokenizedTransaction]

class Explanation(BaseModel):
    log_likelihood: float
    expected_likelihood_range: List[float]
    viterbi_path: List[str]
    anomaly_factors: List[str]
    privacy_note: str = "Server only saw tokens and bins"

class TokenizedResponse(BaseModel):
    transaction_id_token: str
    fraud_score: float
    confidence: float
    verdict: Literal["LEGIT","REVIEW","FRAUD_SUSPECTED"]
    explanation: Explanation

history_db={}
STATE_NAMES=["S1_Low_Local","S2_Med_Local","S3_High","S4_Travel","S5_Online_Risky"]
def sigmoid(x): return 1/(1+np.exp(-x))

def model_path(client_id, customer_token, card_token):
    safe_client = re.sub(r'[^a-zA-Z0-9_-]', '_', client_id)
    return f"/tmp/{safe_client}_{customer_token}_{card_token}_hmm.npz"

@app.post("/v1/train/{customer_token}/{card_token}")
def train(customer_token: str, card_token: str, req: TrainRequest, auth=Depends(require_mtls_client)):
    require_token_format(customer_token)
    require_token_format(card_token)
    for t in req.transactions:
        require_token_format(t.customer_token)
        require_token_format(t.card_token)
        require_token_format(t.transaction_id_token)
    if len(req.transactions) < 10:
        raise HTTPException(400, "Need >=10 tokenized transactions")
    symbols = [t.observation_symbol for t in req.transactions]
    sequences=[]; cur=[]
    for s in symbols:
        cur.append(s)
        if len(cur)>=20:
            sequences.append(cur); cur=[]
    if cur: sequences.append(cur)
    model=SecretHMM(n_states=5, n_symbols=512)
    model.train(sequences, n_iter=15)
    path = model_path(auth["client_id"], customer_token, card_token)
    model.save(path)
    lls=[model.score_log_likelihood(s) for s in sequences]
    history_db[(auth["client_id"], customer_token, card_token)] = {"lls": lls}
    return {"status":"trained", "client_id": auth["client_id"], "model":"SecretHMM per customer", "n_sequences":len(sequences), "privacy":"no PII stored"}

@app.post("/v1/score", response_model=TokenizedResponse)
def score(req: TokenizedTransaction, auth=Depends(require_mtls_client)):
    require_token_format(req.customer_token)
    require_token_format(req.card_token)
    require_token_format(req.transaction_id_token)
    ServerQuantizer().validate_symbol(req.observation_symbol)
    key = (auth["client_id"], req.customer_token, req.card_token)
    path = model_path(auth["client_id"], req.customer_token, req.card_token)
    if not os.path.exists(path):
        return TokenizedResponse(
            transaction_id_token=req.transaction_id_token,
            fraud_score=0.5, confidence=0.2, verdict="REVIEW",
            explanation=Explanation(log_likelihood=0, expected_likelihood_range=[-50,-10], viterbi_path=[], anomaly_factors=["cold_start_no_history"], privacy_note="Server only sees tokens")
        )
    model=SecretHMM.load(path)
    lls = history_db.get(key, {"lls":[-30,-20]})["lls"]
    loglik = model.score_log_likelihood([req.observation_symbol])
    v_path = [STATE_NAMES[i] for i in model.viterbi([req.observation_symbol])]
    mean_ll=np.mean(lls); std_ll=np.std(lls)+1e-6
    z=(loglik-mean_ll)/std_ll
    fraud_score=float(1 - (1/(1+np.exp(-z))))
    verdict="LEGIT" if fraud_score<0.3 else "REVIEW" if fraud_score<0.7 else "FRAUD_SUSPECTED"
    factors=[]
    if req.amount_bin >=6: factors.append("amount_bin_high")
    if req.country_code not in ["US","CA"]: factors.append("location_velocity")
    return TokenizedResponse(
        transaction_id_token=req.transaction_id_token,
        fraud_score=round(fraud_score,4),
        confidence=0.88 if len(lls)>50 else 0.6,
        verdict=verdict,
        explanation=Explanation(
            log_likelihood=round(loglik,2),
            expected_likelihood_range=[round(float(np.percentile(lls,5)),2), round(float(np.percentile(lls,95)),2)],
            viterbi_path=v_path,
            anomaly_factors=factors,
            privacy_note=f"Scored by client {auth['client_id']} - server only saw tok_* and bin {req.amount_bin}"
        )
    )

@app.get("/")
def root():
    return {"message":"SecretModel - Each customer is an HMM. Server only sees tokens. mTLS required."}
