# Author: Meta AI in consultation with edwest19 - SecretModel

import numpy as np
class SecretHMM:
    def __init__(self, n_states=5, n_symbols=512, random_state=42):
        self.N=n_states; self.M=n_symbols
        rng=np.random.default_rng(random_state)
        self.A=rng.dirichlet(np.ones(n_states), size=n_states)
        self.B=rng.dirichlet(np.ones(n_symbols), size=n_states)
        self.Pi=rng.dirichlet(np.ones(n_states))
    def _forward(self, O):
        T=len(O)
        alpha=np.zeros((T,self.N))
        alpha[0]=self.Pi*self.B[:,O[0]]
        c=np.zeros(T)
        c[0]=alpha[0].sum() or 1
        alpha[0]/=c[0]
        for t in range(1,T):
            alpha[t]=(alpha[t-1]@self.A)*self.B[:,O[t]]
            c[t]=alpha[t].sum() or 1
            alpha[t]/=c[t]
        return alpha,c,float(np.sum(np.log(c)))
    def _backward(self, O,c):
        T=len(O)
        beta=np.zeros((T,self.N))
        beta[-1]=1.0/c[-1]
        for t in range(T-2,-1,-1):
            beta[t]=(self.A@(self.B[:,O[t+1]]*beta[t+1]))/c[t]
        return beta
    def train(self, O_sequences, n_iter=20):
        for _ in range(n_iter):
            A_num=np.zeros((self.N,self.N)); A_den=np.zeros(self.N)
            B_num=np.zeros((self.N,self.M)); B_den=np.zeros(self.N)
            Pi_new=np.zeros(self.N)
            for O in O_sequences:
                if len(O)<2: continue
                alpha,c,_=self._forward(O)
                beta=self._backward(O,c)
                T=len(O)
                gamma=alpha*beta
                gamma/=gamma.sum(axis=1,keepdims=True)+1e-12
                Pi_new+=gamma[0]
                for t in range(T-1):
                    xi=(alpha[t][:,None]*self.A)*(self.B[:,O[t+1]]*beta[t+1])[None,:]
                    xi/=xi.sum()+1e-12
                    A_num+=xi; A_den+=gamma[t]
                for t in range(T):
                    B_num[:,O[t]]+=gamma[t]
                    B_den+=gamma[t]
            self.A=(A_num+1e-3)/(A_den[:,None]+1e-3*self.N)
            self.B=(B_num+1e-3)/(B_den[:,None]+1e-3*self.M)
            self.Pi=(Pi_new+1e-3)/(Pi_new.sum()+1e-3*self.N)
    def score_log_likelihood(self,O):
        _,_,ll=self._forward(O); return ll
    def viterbi(self,O):
        T=len(O)
        delta=np.zeros((T,self.N)); psi=np.zeros((T,self.N),dtype=int)
        delta[0]=np.log(self.Pi+1e-12)+np.log(self.B[:,O[0]]+1e-12)
        for t in range(1,T):
            for j in range(self.N):
                seq=delta[t-1]+np.log(self.A[:,j]+1e-12)
                psi[t,j]=int(np.argmax(seq))
                delta[t,j]=float(np.max(seq)+np.log(self.B[j,O[t]]+1e-12))
        path=[int(np.argmax(delta[-1]))]
        for t in range(T-1,0,-1):
            path.insert(0,int(psi[t,path[0]]))
        return path
    def save(self,path): np.savez(path,A=self.A,B=self.B,Pi=self.Pi,N=self.N,M=self.M)
    @classmethod
    def load(cls,path):
        d=np.load(path); m=cls(n_states=int(d['N']),n_symbols=int(d['M'])); m.A=d['A']; m.B=d['B']; m.Pi=d['Pi']; return m
HiddenMarkovModel = SecretHMM
