# Agent handoff: FiltSyn / CircuitRF filter plugin

Read this first if you are continuing the work. The user is Fa (Thailand, RF/SDR/5G NR, Mac user, uses the CircuitRF
simulator). Fa wants a Nuhertz-like filter synthesis tool as a CircuitRF plugin. Keep answers short, show results as
tables, and always verify with real EM before claiming something works.

## 1. State at handoff

Working and shipped (osx-arm64): coupled-line, hairpin, open-loop-ring chain and four-pole cross-coupled ring
(quasi-elliptic) bandpass synthesis; circuit-model response with a lossy trace and Qu / insertion-loss report; layout;
cell placement; EM calibration loop; Rogers material library with per-cell technology files. 166 tests pass. The
EM-validated examples are in `README.md`. Fa has Hong and Lancaster, *Microstrip Filters for RF/Microwave Applications*
(2001) as a PDF; the quasi-elliptic filter, the bend model and the loss estimate come from it (sections 10.1, 4.3.1, 3.6).

Source of truth: the git repo (this one). Local copy on Fa's Mac: `/Users/fa/Desktop/Projects/filtsyn`. Fa's CircuitRF
install lives at `/Users/fa/Desktop/Projects/circuitRF`; scratch EM workspace
`/Users/fa/Desktop/Projects/circuitRF/.circuitrf-scratch/em_cmp` (contains `.cws`, `tech/`, cells BASE1, OPT1, IT1, IT2,
OLR1, OLR2, OLR58, OLR58B and their `*_em.s2p` results).

## 2. Architecture

- `FiltSyn.Core` (no UI): `Prototype`, `CouplingMatrix` (ideal response), `Microstrip`/`Stripline` (+ `Substrate`),
  `CoupledMicrostrip`, `MulticonductorLine` (N-conductor coupled line Y-matrices, even/odd nearest-neighbour),
  `CoupledLineFilter` (+tuner/optimizer/verification), `HairpinSynthesizer` / `HairpinCalibration` /
  `HairpinCircuitModel` / `HairpinTuner` / `HairpinVerification` / `HairpinLayout`, `MicrostripGap`, `NelderMead`,
  `CouplingCurves` (`TabulatedCouplingCurve`), `CircuitRfNetlist`.
- `FiltSyn.Plugin`: `Program` (protocol loop: hello/update/action/commit/bye; `Reply`, `Calibrate`), `DesignEngine`
  (params to `FilterResult`; hairpin/ring/coupled/cross-coupled branches; `LossInfo`), `CellWriter` (`.clay/.c3d/.cem`, EM window), `EmCalibration`,
  `Touchstone`, `TechInfo`, `Views` (plot/artwork/table, `PortGeometry`), `Wire` (frames), `WorkspaceLinker`.
- `circuitrf-plugin/plugin.json`: manifest (params, UI layout). Param names used by code: `filter` (choice
  Coupled-line | Hairpin | Open-loop ring), `orientation` (Alternating | Same), `tipGap`, `response`, `order`, `f0`, `bw`,
  `ripple`, `optimize`, `medium`, `technology`, `substrateSource` (From technology | Rogers material | Manual), `material`,
  `zeroOmega`, `er/h/tand/copper`, `f0Corr`, `bwCorr`, `emResult`,
  `crfCheck`, `cellName`, `emSpan`, `emPoints`, readouts `summary`, `dims`.

### Cross-coupled ring internals (Nov 2026, newest)
- `QuasiEllipticPrototype` (`QuasiElliptic.cs`): `FromCurveFit(a)` = Hong eq. 10.6; `Synthesize(a, rl)` solves g1, g2, J2
  for equiripple (interior local maximum, centre and band edge at -RL) with J1 from eq. 10.13, by continuation from the
  fit range. `ToBandpass` gives a `GeneralCouplingMatrix` (full M, eq. 10.9). Valid Omega_a 1.3 to 6.
- `RingNetwork` (`RingNetwork.cs`): rings = `RingPlacement(X0, Y0, Gap side, Side?)`; segments in path order from the
  first tip (tail, 3 sides, tail); facing sides of two rings (rows/columns separated, not diagonal) become coupled pieces
  (2-conductor `MulticonductorLine`, cached per gap); splits are mirrored between partners; corner shortening half on each
  side of a corner as a dead zone; union-find merges tiny pieces. `Evaluate` (2 taps), `OnePort`, `ExternalQ` (S11 group
  delay), `NaturalFrequencies` (probe at ring 0's first tip), `PointAt`.
- `CrossCoupledRingSynthesizer` (`CrossCoupledRing.cs`): tune side, curves (electric: gaps facing; magnetic: folds facing;
  mixed: rows, with the bottom offset), invert on the monotonic branch (electric k passes a null near 1.5 to 2 mm on
  RO4350B), tap from Qe on outer sides only, then `Optimize` (top/bottom side scale, 3 gaps, tap; Nelder-Mead 700 evals on
  121 points). `CrossCoupledDesign.Placements()` is symmetric about the column axis; `CrossCoupledLayout.From`.
- Plugin: filter `Cross-coupled ring (quasi-elliptic)`, param `zeroOmega`; `DesignEngine.CrossCoupled` caches the design by
  key; resonator Z0 falls back 50/70/90/110 ohm like the chain.

### Open-loop ring internals
- `HairpinGeometry` has `TipGap` (tail length `(BaseLength - TipGap)/2` when > 0) and `Alternate`.
  `HairpinCircuitModel.Assemble` builds nodes per ring: upright rings fold at y=0 (b-nodes), flipped rings fold at the top
  (t-nodes); tails + pi gap network on the open-end nodes; taps split the arms at `l1` and `arm - l1`.
- `HairpinSynthesizer.SynthesizeCalibrated(sub, g, f0, bw, options, cal)`; `HairpinOptions { TipGap, Alternate, FeedZ0 }`.
  The non-calibrated `Synthesize` throws for rings. `BuildLayout` flips rings and mirrors feeds (`HairpinTap.FromTop`).
- `HairpinCalibration.Run(..., tipGap, square, alternate)` finds the arm length for resonance and the coupling curve
  (square mode brackets 0.5 to 1.6 of lambda/8); coupling `kMax`/`GapAtMaxCoupling`.
- `HairpinLayout` emits C-shaped 12-point polygons for rings, mirrored in y for flipped ones.
- `DesignEngine` caches the calibration with key `(substrate, f0, tipGap, alternate)`.
- `CellWriter` widens the EM window for rings to at least +/-12 % of f0.

## 3. CircuitRF plugin contract (apiVersion 1) and gotchas

- Framed stdin/stdout: `[u32 jsonLen][u32 binLen][json][int64 payload]`, little endian. Host calls may arrive during
  actions: always answer `host` messages (the harness loop does).
- `file` params are REQUIRED by the host: use `string` for optional paths (that caused the "emResult: a file is required" bug).
- Replies can carry `set: {param: value}`, `enabled: {action/param: bool}`, `messages`, `views`, `readouts`.
- `circuitRF check` only accepts plugin folders under the CircuitRF server root: copy the plugin to
  `.circuitrf-scratch/fsplugin` and check there.
- Running EM from the agent tools: `circuitrf run analysis=em path=<cell .cem> workspace=<path to the .cws FILE, not the folder>
  output=<x.s2p>`; use `async=true` then poll; a cell takes about 10 to 60 s. A placed cell's `.cem` has
  `"LayoutRef": "../3d/<name>.c3d"`: when testing from a workspace subfolder rewrite it to `<name>/3d/<name>.c3d`, and make
  sure `TechRef` points at an existing `tech/*.ctech` (the harness default is a stripline tech).
- CircuitRF `MGAP` element: quasi-static fits, valid 0.5<=W/h<=2, 0.1<=S/W<=1, 2.5<=Er<=15; substrate only from technology.
- macOS binaries must be signed (`rcodesign sign in out`, then `chmod +x`) or they are killed on Apple Silicon.

## 4. Environment recipes

- .NET 9 at `$HOME/dotnet` in the cloud container: `export PATH=$HOME/dotnet:$PATH`. Cloud is x86_64; cross-publish osx-arm64:
  `tools/build-plugin.sh osx-arm64`.
- Test the plugin without CircuitRF: `tools/plugin_harness.py <exe> <staging> [crf|hairpin|olr]` with env `OPT`, `CELL`,
  `F0_MHZ`, `BW_MHZ`, `F0CORR`, `BWCORR`, `ORIENT`, `EMFILE`. Linux build: `dotnet publish src/FiltSyn.Plugin -c Release -r linux-x64
  --self-contained true -p:PublishSingleFile=true -o /tmp/pub/linux-x64`.
- Deliver to Fa's Mac from a cloud session: `SendUserFile` (gives a file_uuid), then `device_commit_files`; then `chmod +x` on
  the device. The device shell cannot overwrite files by unlinking: extract tarballs with `tar xzf --overwrite`.
- Typical EM iteration for a new cell: generate via harness, tar, upload, extract into the scratch workspace, run EM
  (async), parse the `.s2p` (RI format, GHz) for peak, -3 dB edges (centre = geometric mean), best S11.

## 5. Conventions

- Measure bandwidth at -3 dB below the peak; centre is the geometric mean of the edges (`HairpinCircuitModel.Metrics`).
  Do not quote S11 at the -3 dB edges as return loss (it is about 3 dB by definition); use the ripple band.
- Calibration update is multiplicative and clamped to 0.5 to 2 (`EmCalibration`).
- Keep non-alternate (plain hairpin) numerical results identical when touching `Assemble`; the 151 older tests guard it.
- Prefer verification by real EM over circuit-model arguments. Say plainly what was and was not verified.

## 5b. Accuracy findings (Nov 2026)

- EM reference: use circuitRF's **planar** solver (AnalysisKind Planar, no SignalStackupLayerName needed) or EMerge at
  **Accurate**. EMerge Standard reads 3 to 4 % low on these resonators; do not calibrate against it.
- Eigenmode (EMerge) on a single ring took over 5 min and stalled with absorbing walls; use driven runs with weak taps
  instead (scratch cells `SP*` in `em_cmp`, generator logic in the history of this file's commit).
- Corner correction: `HairpinGeometry.CornerShortening` (NaN means `DefaultCornerShortening`), microstrip only. Rings:
  0.80 x Gupta's bend shortening (`BendShortening`, Hong eq. 4.42, W/h held >= 1.2), anchored to 0.40 W on RO4350B
  0.508 mm, clamped 0.25 to 0.5 W; checked on RT/duroid 5880 0.787 mm (EM 0.30 to 0.32 W, predicted 0.31 W). Hairpins:
  0.64 / 0.67 / 0.844 W at Er 2.2 / 3.66 / 10.2, piecewise linear. Rings cap at 0.52 W (RO3010 measured 0.518 W).
  RO3010 cells CKR24/CKR58/CKH24 in `em_cmp` are not run yet (slow: use ~61 points, about 10 min each).
  Generator: `/tmp/cf` C# tool in the session (gen/fit modes; regenerate from the description here if lost). A tap inside a corner region is clamped to
  the corner so the fold keeps its corrected length (this mattered: without it narrowband hairpin taps landed in the dead
  zone and broke the response).
- Open questions: the hairpin/ring difference (open ends vs coupled arms) and the tapped straight line coming out 3.3 %
  low were not resolved; a T-junction model for the taps is the likely next physics step.

- Coupling check (Nov 2026, cells QE36, RPE, RPX, RPM, HP2, HQ1 in `em_cmp`, weak taps; RPM first had taps near the fold
  corners and gave one broad hump, redone with 0.4 mm gap-coupled probes): resonances and electric coupling agree; mixed
  coupling is overestimated (rings 0.0263 vs EM 0.0208 at 0.98 mm; hairpins 0.0166 vs about 0.004 at 1.885 mm). Dispersion
  is not the cause (Getsinger per-mode dispersion moves the even/odd split by 1 to 3 %); the likely cause is the corner
  regions at the ends of the coupled sections, which the model drops while the net mixed coupling is a near-cancellation.
  VH30 (hairpin 3 GHz / 60 MHz) failed in EM for this reason. QE36 EM: lower zero 3.478 GHz (ideal 3.482), centre
  3.562, 3 dB bandwidth 72 MHz (designed 120), loss 4.5 dB.

## 6. Suggested next steps (not done)

-1. Fix mixed coupling: sweep EM ring and hairpin pairs over gaps 0.2 to 3 mm (weak gap-coupled probes, about 4 min each),
   then either model the corner-region coupling in `RingNetwork`/`HairpinCircuitModel` or fit a correction to the mutual
   terms; re-run QE36 and VH30.

0. Validate the corner constants on a second substrate (e.g. RT5880 0.787 mm, W/h about 3) and make them a function of W/h if needed.


1. **Bend/gap-end compensation** for rings: model the roughly constant (about 1 mm) path error from corners and gap ends so the
   first EM run lands closer; fit against the EM results for 3.6 and 5.8 GHz (and add a third frequency).
2. **Cap gaps at the technology's minimum feature** in the optimiser and in synthesis; warn when a design needs less.
3. **Auto-calibrate loop**: run EM from the plugin via host services (`simulate`), iterate until within tolerance.
4. Better ring optimisation: free individual gaps and taps (currently mirror-symmetric groups), weight the in-band return loss.
5. **Lossy circuit model for rings** (conductor and dielectric loss) so insertion loss agrees with EM.
6. Other topologies: interdigital, combline, stepped-impedance lowpass, SIR; flipped (alternating) hairpins for stripline.
7. Cross-platform plugin binaries (win-x64, linux) and a GitHub Release with the signed osx-arm64 binary.
8. Add EM regression fixtures (the `.s2p` files from OLR/BASE cells) under `tests/` or `docs/` for tolerance checks.

## 7. Verification checklist before shipping a change

1. `dotnet test` all green.
2. `tools/plugin_harness.py` for each filter type (`crf`, `hairpin`, `olr`) returns a reply and commits a cell.
3. Place a cell, run EM, compare centre/bandwidth/S11 with the circuit model; apply the calibration loop once.
4. `circuitRF check` on the plugin folder; rebuild, sign and `chmod +x` the osx-arm64 binary.
