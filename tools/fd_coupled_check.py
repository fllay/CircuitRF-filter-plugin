"""Independent 2D finite-difference check of coupled microstrip even/odd modes (zero-thickness strips).
Usage: python3 fd_coupled_check.py   (needs numpy, scipy). Prints Z0e, Z0o, eps_e, eps_o.
Used to sanity-check FiltSyn.Core.CoupledMicrostrip (expect agreement within a few percent)."""
import numpy as np, scipy.sparse as sp, scipy.sparse.linalg as spl, sys

C0 = 299792458.0

def energy(er, h, w, gap, mode, n=16, xspan=25, yspan=15):
    dx = h / n
    nx = int(round((2 * xspan * h + 2 * w + gap) / dx)) + 1
    ny = int(round(yspan * h / dx)) + 1
    jh = int(round(h / dx))                       # substrate interface row
    x = np.arange(nx) * dx
    xc = x[-1] / 2
    s1 = (xc - gap / 2 - w, xc - gap / 2)
    s2 = (xc + gap / 2, xc + gap / 2 + w)
    N = nx * ny
    idx = lambda i, j: i * ny + j
    # cell permittivity (cell between nodes i..i+1, j..j+1)
    eps_cell = np.ones((nx - 1, ny - 1))
    eps_cell[:, :jh] = er
    fixed = np.zeros(N, bool); val = np.zeros(N)
    fixed[[idx(i, 0) for i in range(nx)]] = True
    fixed[[idx(i, ny - 1) for i in range(nx)]] = True
    fixed[[idx(0, j) for j in range(ny)]] = True
    fixed[[idx(nx - 1, j) for j in range(ny)]] = True
    v2 = 1.0 if mode == 'even' else -1.0
    for i in range(nx):
        if s1[0] - 1e-12 <= x[i] <= s1[1] + 1e-12: fixed[idx(i, jh)] = True; val[idx(i, jh)] = 1.0
        if s2[0] - 1e-12 <= x[i] <= s2[1] + 1e-12: fixed[idx(i, jh)] = True; val[idx(i, jh)] = v2
    rows, cols, data = [], [], []
    # horizontal edges (i,j)-(i+1,j): average of cells above/below; vertical edges similarly
    def add(a, b, g):
        rows.extend([a, b, a, b]); cols.extend([a, b, b, a]); data.extend([g, g, -g, -g])
    I, J = np.meshgrid(np.arange(nx - 1), np.arange(ny), indexing='ij')
    up = np.where(J < ny - 1, eps_cell[I, np.minimum(J, ny - 2)], 0.0)
    dn = np.where(J > 0, eps_cell[I, np.maximum(J - 1, 0)], 0.0)
    gh = 0.5 * (up + dn)
    ge_h = (I, J, gh)
    I2, J2 = np.meshgrid(np.arange(nx), np.arange(ny - 1), indexing='ij')
    rt = np.where(I2 < nx - 1, eps_cell[np.minimum(I2, nx - 2), J2], 0.0)
    lf = np.where(I2 > 0, eps_cell[np.maximum(I2 - 1, 0), J2], 0.0)
    gv = 0.5 * (rt + lf)
    a = (I * ny + J).ravel(); b = ((I + 1) * ny + J).ravel(); g = gh.ravel()
    a2 = (I2 * ny + J2).ravel(); b2 = (I2 * ny + J2 + 1).ravel(); g2 = gv.ravel()
    A = np.concatenate([a, a2]); B = np.concatenate([b, b2]); G = np.concatenate([g, g2])
    L = sp.coo_matrix((np.concatenate([G, G, -G, -G]), (np.concatenate([A, B, A, B]), np.concatenate([A, B, B, A]))), shape=(N, N)).tocsr()
    free = ~fixed
    rhs = -L[free][:, fixed] @ val[fixed]
    phi = val.copy()
    phi[free] = spl.spsolve(L[free][:, free].tocsc(), rhs)
    W = 0.5 * phi @ (L @ phi)
    return W    # = (C11 +/- C12) * V^2 for V = 1

def modes(er, h, w, gap):
    out = {}
    for mode in ('even', 'odd'):
        c = energy(er, h, w, gap, mode); ca = energy(1.0, h, w, gap, mode)
        out[mode] = (1.0 / (C0 * np.sqrt(c * ca * 1e-24 / 1e-24)) if False else None, c / ca, c, ca)
    return out

if __name__ == '__main__':
    eps0 = 8.8541878128e-12
    er, h, w = 3.55, 0.508e-3, 1.1e-3
    for gap in (0.2e-3, 0.5e-3, 1.0e-3, 3.0e-3):
        r = {}
        for mode in ('even', 'odd'):
            c = energy(er, h, w, gap, mode) * eps0
            ca = energy(1.0, h, w, gap, mode) * eps0
            r[mode] = (1.0 / (C0 * np.sqrt(c * ca)), c / ca)
        print(f"gap={gap*1e3:.2f}mm Z0e={r['even'][0]:.2f} Z0o={r['odd'][0]:.2f} ee={r['even'][1]:.3f} eo={r['odd'][1]:.3f}")
