"""2D finite-difference check of coupled stripline even/odd Z0 (zero-thickness, er=1) against Cohn closed forms. numpy + scipy required."""
import numpy as np, scipy.sparse as sp, scipy.sparse.linalg as spl
from scipy.special import ellipk
eps0=8.8541878128e-12; C0=299792458.0
def W(b,w,s,mode,n=40,span=4):
    dx=b/n; half=(w+s/2)+span*b
    nx=int(round(2*half/dx))+1; ny=n+1
    x=(np.arange(nx)-(nx-1)/2)*dx
    N=nx*ny; idx=lambda i,j:i*ny+j
    fixed=np.zeros(N,bool); val=np.zeros(N)
    for i in range(nx): fixed[idx(i,0)]=fixed[idx(i,ny-1)]=True
    for j in range(ny): fixed[idx(0,j)]=fixed[idx(nx-1,j)]=True
    j=n//2
    for i in range(nx):
        ax=abs(x[i])
        if s/2-1e-12<=ax<=s/2+w+1e-12 and (s>0 or True):
            fixed[idx(i,j)]=True; val[idx(i,j)]=1.0 if (mode=='even' or x[i]>0) else -1.0
    rows=[];cols=[];dat=[]
    I,J=np.meshgrid(np.arange(nx-1),np.arange(ny),indexing='ij')
    a=(I*ny+J).ravel();bb=((I+1)*ny+J).ravel()
    I2,J2=np.meshgrid(np.arange(nx),np.arange(ny-1),indexing='ij')
    a2=(I2*ny+J2).ravel();b2=(I2*ny+J2+1).ravel()
    A=np.concatenate([a,a2]);B=np.concatenate([bb,b2]);G=np.ones(len(A))
    L=sp.coo_matrix((np.concatenate([G,G,-G,-G]),(np.concatenate([A,B,A,B]),np.concatenate([A,B,B,A]))),shape=(N,N)).tocsr()
    free=~fixed
    phi=val.copy(); phi[free]=spl.spsolve(L[free][:,free].tocsc(),-L[free][:,fixed]@val[fixed])
    return 0.5*phi@(L@phi)*eps0   # (C11±C12)*... for V=1 both strips -> energy of both
def single(b,w,n=40):
    # one strip: use s large? emulate with mode even and s=0 gives 2w; do direct
    pass
def K(m): return ellipk(m)  # parameter m=k^2
def zsingle(b,w):
    k=np.tanh(np.pi*w/(2*b)); return 30*np.pi*ellipk(1-k*k)/ellipk(k*k)
def cand(b,w,s):
    a=np.pi*w/(2*b); sg=np.pi*(w+s)/(2*b)
    out={}
    ke=np.tanh(a)*np.tanh(sg); ko=np.tanh(a)/np.tanh(sg)
    out['A']=(30*np.pi*ellipk(1-ke**2)/ellipk(ke**2), 30*np.pi*ellipk(1-ko**2)/ellipk(ko**2))
    # B: Cohn exact: ke=tanh(a)... alt with k via sech
    return out
b=1.0
for w,s in [(0.5,0.3),(0.4,0.5),(0.5,1.0)]:
    ze=1/(C0*W(b,w,s,'even')); zo=1/(C0*W(b,w,s,'odd'))
    print(w,s,'FD',ze,zo,'cand',cand(b,w,s))
print('s->0 check', zsingle(1,1.0), cand(1,0.5,1e-9))
for n in (20,40,80):
    print(n, 1/(C0*W(1,0.5,0.3,'even',n=n)), 1/(C0*W(1,0.5,0.3,'odd',n=n)))
