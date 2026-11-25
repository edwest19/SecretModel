# Author: Meta AI in consultation with edwest19 - SecretModel

import numpy as np
from sklearn.cluster import KMeans
MCC_GROUPS = {"grocery":0,"gas":1,"restaurant":2,"retail":3,"travel":4,"online_market":5,"atm":6,"other":7}
class ClientQuantizer:
    def __init__(self, n_amount_clusters=8):
        self.n_amount_clusters=n_amount_clusters
        self.kmeans=None
        self.is_fitted=False
    def fit(self, amounts):
        if len(amounts) < self.n_amount_clusters:
            self.bins=np.quantile(amounts, np.linspace(0,1,self.n_amount_clusters+1))
            self.kmeans=None
            self.is_fitted=True
            return
        X=np.log1p(np.array(amounts)).reshape(-1,1)
        self.kmeans=KMeans(n_clusters=self.n_amount_clusters, n_init=10, random_state=42)
        self.kmeans.fit(X)
        self.is_fitted=True
    def quantize_to_bin(self, amount: float) -> int:
        if self.kmeans:
            return int(self.kmeans.predict(np.array([[np.log1p(amount)]]))[0])
        return int(np.digitize(amount, self.bins)-1) % self.n_amount_clusters
    def quantize_to_symbol(self, amount: float, category: str, hour: int, is_online: bool) -> int:
        a = self.quantize_to_bin(amount)
        m = MCC_GROUPS.get(category.lower(),7)
        t = 0 if hour<6 else 1 if hour<12 else 2 if hour<18 else 3
        o = 1 if is_online else 0
        return ((a*8+m)*4+t)*2+o
