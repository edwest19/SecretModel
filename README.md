# SecretModel
SecretModel is a test and development fraud-detection service. Each payment card is modeled as its own Hidden Markov Model. A historical journal of tokenized test debit-card transactions arrives by SFTP and trains the per-card models. Real-time scoring requests are evaluated against those models over HTTPS with mutual TLS.
