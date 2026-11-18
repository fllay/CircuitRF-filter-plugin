# CircuitRF Filter Plugin (FiltSyn)

An RF/microwave bandpass filter synthesis tool in C# (.NET 9), in the spirit of Nuhertz Filter Solutions, packaged as a
**CircuitRF plugin**. Type a specification, see the circuit-model response and the layout, and place a ready-to-simulate
cell (layout, 3D view, EMerge setup) in a CircuitRF workspace. A desktop app (Avalonia) shares the same core library.

Filter types: **coupled-line (parallel-coupled)**, **hairpin**, **square open-loop ring chain** (alternating or same
orientation) and the **four-pole cross-coupled open-loop ring (quasi-elliptic)** filter of Hong and Lancaster section 10.1,
with a transmission zero either side of the passband. A closed **EM calibration loop** corrects the systematic gap between the closed-form circuit models and
full-wave EM.

For anyone (human or agent) continuing the work, read [`docs/AGENT_HANDOFF.md`](docs/AGENT_HANDOFF.md).

## Repository layout

| Path | Contents |
|---|---|
| `src/FiltSyn.Core` | UI-free library: prototypes, microstrip/stripline models, coupled-line/hairpin/ring synthesis, circuit models, tuners, layout |
| `src/FiltSyn.Plugin` | The CircuitRF plugin executable (framed stdin/stdout protocol), cell writer, EM calibration |
| `src/FiltSyn.App` | Avalonia desktop app (ScottPlot response plot, layout view, DXF export) |
| `circuitrf-plugin/` | Plugin folder to add in CircuitRF: `plugin.json` manifest, plugin README, `bin/<rid>/` binaries (not in git) |
| `tests/FiltSyn.Core.Tests` | xUnit tests (156 at last run) |
| `tools/` | `build-plugin.sh`, `plugin_harness.py` (fake CircuitRF host), 2D finite-difference checkers |
| `docs/AGENT_HANDOFF.md` | Detailed state, conventions, pitfalls and next steps for a continuing agent |

## Build, test, run

    export PATH=$HOME/dotnet:$PATH          # .NET 9 SDK
    dotnet test                             # 156 tests
    dotnet run --project src/FiltSyn.App    # desktop app
    tools/build-plugin.sh osx-arm64         # plugin binary into circuitrf-plugin/bin/osx-arm64/

A macOS binary must be signed (ad hoc is enough) or Apple Silicon kills it at launch. `build-plugin.sh` signs with
`codesign` on a Mac; elsewhere sign the result with `rcodesign sign in out`. Add each built rid to `run` in
`circuitrf-plugin/plugin.json`. Binaries are git-ignored; build them or attach them to a release.

## Beta releases (GitHub Actions)

`.github/workflows/release.yml` runs the tests, builds the plugin for osx-arm64, osx-x64, linux-x64, linux-arm64 and
win-x64 (single-file, self-contained; macOS ad hoc signed), and zips each with a `plugin.json` whose `run` map lists that
platform (`tools/package-plugin.py`). Push a tag to publish a GitHub **pre-release** with the per-platform zips and an
all-platform zip:

    git tag v0.2.0-beta.1 && git push origin v0.2.0-beta.1

"Run workflow" in the Actions tab builds the same zips as workflow artifacts without releasing. macOS builds are not
notarised: if Gatekeeper blocks one, run `xattr -dr com.apple.quarantine filtsyn-plugin`.

## Installing the plugin in CircuitRF

Add `circuitrf-plugin/` (the folder holding `plugin.json`) under Tools > Plugins, then open Tools > Plugins > Filters >
Filter Synthesis. Validate the manifest with `circuitRF check <folder>` (the checker only accepts paths under the
CircuitRF server root).

## Using it

1. Pick **Medium** (microstrip/stripline) and a **Technology**; Substrate *From technology* reads Er, tan d, thickness
   and copper from the technology's stackup, *Rogers material* takes Er and tan d from a built-in library of 21 Rogers
   laminates (RT/duroid 5870/5880/6002/6006/6010LM, RO3003/3203/3006/3010/3035, RO4003C/4350B/4360G2/4730G3/4835,
   TMM3/4/6/10/10i/13i; thickness and copper typed in), or choose *Manual*. With *Rogers material* or *Manual* the placed
   cell gets its own technology file, `tech/fs_<material>_<mil>mil_<copper>um.ctech`, a copy of the selected technology
   with that dielectric, so EM and circuitRF's circuit check simulate the substrate you chose.
2. Choose filter type, response (Chebyshev/Butterworth), order, centre frequency, bandwidth, ripple. For open-loop rings
   also **Ring orientation** and **open gap**. The Response tab overlays the circuit model on the ideal prototype.
3. **Place cell in workspace** writes `layout/<name>.clay`, `3d/<name>.c3d` and `em/<name>.cem` (EMerge, driven).
4. Run EM in CircuitRF, paste the `.s2p` path into **EM result path**, press **Calibrate from EM result**, place the cell
   again, repeat until the EM centre and bandwidth settle. **Reset calibration** clears the factors. Leave the EM path empty
   for a first design.

## What has been done (history)

1. **Core synthesis.** Butterworth/Chebyshev prototypes, order estimation, coupling matrix, ideal S-parameters.
2. **Transmission-line media.** Microstrip: Hammerstad-Jensen Z0/eps_eff with thickness, Kirschning-Jansen dispersion,
   conductor/dielectric loss and Q, coupled-line even/odd modes, width and gap solvers (validated against scikit-rf and
   a 2D FD solver). Stripline: Cohn exact formulas, Wheeler thickness correction.
3. **Coupled-line bandpass** (N+1 quarter-wave sections, J-inverter equations, centring tuner, circuit model).
4. **Hairpin bandpass**: physical synthesis, multiconductor circuit model (all arms as one coupled line), design-curve
   calibration (`HairpinCalibration`), response centring and Nelder-Mead polish, ideal-vs-circuit verification.
5. **Layout and export**: layout preview, DXF export (R12 ASCII, mm).
6. **CircuitRF plugin**: panel plugin (`fa.filtsyn`, apiVersion 1) with technology-aware substrate, response/layout/
   dimension views, cell placement (`.clay`, `.c3d`, `.cem`), optional check against CircuitRF's own circuit models.
7. **Circuit-model optimisation and CircuitRF model check** (ideas 1 and 4). Optimising on the circuit model and
   comparing with CircuitRF's coupled-line models did not close the gap to EM (circuit models are about 2.5 % high
   versus EM), which led to idea 2.
8. **EM feedback loop** (idea 2). Measure the EM -3 dB centre (geometric mean of the edges) and 3 dB bandwidth, then
   update correction factors multiplicatively: `f0Corr *= f0Spec / fc_meas`, `bwCorr *= targetBW3dB / bw_meas`. On a 3.6 GHz
   hairpin: EM 3.517 GHz / 275 MHz, then 3.595 / 251, then 3.603 GHz / 244 MHz in two iterations (best S11 39.8 dB).
   Productised as the Calibrate / Reset buttons (`EmCalibration.cs`); the EM-result path is optional (a `string`
   parameter, because the host treats `file` parameters as required).
9. **Open-loop ring chain** (this release, details below).

## Open-loop ring filters

A square open-loop resonator is a hairpin whose arms end in tails turned toward each other, leaving a small open gap
(default 0.3 mm). Square geometry: arm length equals the base centreline length `a`; inner gap `a - w`; tail
`t = (a - tipGap)/2`; resonator path `4a - tipGap`.

- **Gap model** (`MicrostripGap`): a pi network, series Cg and shunt Cp per end, from quasi-static Garg-Bahl style fits
  (the same family as CircuitRF's `MGAP` element). Validity: 0.5 <= W/h <= 2, 0.1 <= S/W <= 1, 2.5 <= Er <= 15. Microstrip only.
- **Same orientation**: neighbouring open ends face each other, so electric and magnetic coupling cancel; k is capped at
  about 0.02 (about 2.5 % FBW for 4 poles). The tool refuses wider requests and reports the reachable bandwidth.
- **Alternating orientation** (default): every second ring is flipped (open end at the bottom, fold at the top), so the
  couplings add; k reaches 0.14 to 0.19 (about 6 to 8 % FBW for 4 poles). Input is fed from below, output from above.

### EM validation (CircuitRF with EMerge, 4-pole 0.1 dB Chebyshev, RO4350B 0.508 mm, 50 ohm)

| Case | EM centre | EM 3 dB BW | Best S11 |
|---|---|---|---|
| 3.6 GHz, 150 MHz: first design | 3.736 GHz (+3.8 %) | 200 MHz | |
| same, after one calibration step | 3.593 GHz (-0.2 %) | 169 MHz | 23.8 dB |
| 5.8 GHz, 200 MHz: first design | about 6.3 GHz (+8.6 %) | about 180 MHz (clipped) | 20.6 dB |
| same, centre factor 0.92 | 5.746 GHz (-0.9 %) | 244 MHz (target 242) | 31 dB |

The first EM run lands high because bends and gap ends are not modelled; the offset grows with frequency (ring shrinks,
roughly a constant 1 mm of path error). The EM window for rings is therefore at least +/-12 % of f0, so the passband is
always inside it and calibration can measure it. Peak EM insertion loss is about 2.5 to 3.5 dB (conductor/dielectric loss
at about 5 % FBW; the circuit model here is lossless).

## Cross-coupled open-loop ring filter (quasi-elliptic)

Hong and Lancaster, *Microstrip Filters for RF/Microwave Applications* (2001), section 10.1 and Fig. 10.4(a): four square
open-loop rings in a 2 x 2 block. Rings 1 and 4 (top row) carry the taps; 1-2 and 3-4 couple arm to arm (mixed), 2-3
fold to fold (magnetic) and the cross coupling 1-4 through the facing open gaps (electric), which has the opposite sign
and puts a transmission zero either side of the passband. Set **Filter type** to *Cross-coupled ring (quasi-elliptic)*
and **Transmission zeros at +/- this many half-bandwidths** (Omega_a, 1.3 to 6; 2 puts them one bandwidth from the centre).
The return loss follows the Chebyshev ripple (Butterworth: 20 dB); the order is fixed at 4.

How it is designed (`QuasiElliptic.cs`, `RingNetwork.cs`, `CrossCoupledRing.cs`):

1. **Prototype**: g1, g2 and the inverters J1 (cross) and J2 (main) of Hong's Fig. 10.2 ladder. J1 follows exactly from the
   zero position (eq. 10.13 for n = 4: Omega_a^2 g2^2 = J2^2 - J2/J1); g1, g2, J2 are solved for an equiripple return loss,
   starting from Hong's curve fit (eq. 10.6) and stepping out from its 1.8 to 2.4 range. Reproduces Table 10.1 to 4-5 digits.
2. **Coupling matrix** (eq. 10.9): M12 = M34, M23, M14 (opposite sign) and Qe; the ideal response is the general
   coupling-matrix analysis of section 8.1.
3. **Ring network circuit model**: any arrangement of square rings as straight segments; wherever two rings face each
   other, the overlapping parts of the facing sides are an even/odd coupled-line section. This gives electric, magnetic and
   mixed coupling with no fitted constant. Tips use the gap pi model, corners the EM-fitted shortening.
4. **Dimensions**: ring size tuned to f0; k(s) curves for the three coupling kinds (section 8.3, from the split of the
   two natural frequencies) give the gaps; the bottom row shifts sideways so the main and cross gaps can differ; the tap
   comes from Qe by the group delay of S11 of the singly loaded ring (section 8.4, tau = 4 Qe / w0).
5. **Optimisation** of both row sizes (asynchronous tuning, section 8.2.2: the rows are loaded differently by their
   neighbours), the three gaps and the tap on the full four-ring model against the ideal response.

### EM check of the cross-coupled filter and of the coupling model (planar EM, RO4350B 0.508 mm)

| Item | EM | Circuit model |
|---|---|---|
| QE36 filter, 3.6 GHz / 120 MHz, zeros at Omega_a = 2: lower zero | 3.478 GHz (-35 dB) | 3.452 (ideal 3.482) |
| QE36 centre / 3 dB bandwidth / midband loss | 3.562 GHz / 72 MHz / 4.5 dB | 3.597 / 120 / 3.9 |
| Ring pair, open gaps facing (electric), 1.41 mm | single peak at 3.600 GHz, k <= 0.003 | 3.601 GHz, k 0.0037 |
| Ring pair, rows (mixed), 0.98 mm | k 0.0208 (3.561 / 3.636 GHz) | k 0.0263 (3.558 / 3.653) |
| Hairpin pair, 1.885 mm (4-pole 3 GHz / 60 MHz design) | k about 0.004 | k 0.0166 |

These runs showed the coupled-line circuit model gets the *coupling between resonators* wrong even though resonant
frequencies agree. That led to the EM-fitted coupling correction below.

### Coupling correction (EM-fitted, Nov 2026)

Twelve more planar-EM pairs with weak 0.4 mm gap-coupled probes (cells `KE*`, `KM*`, `KX*`, `KH*` in `em_cmp`), k from the
two S21 peaks, against the circuit model's k for the same geometry (RO4350B 0.508 mm):

| Kind | s/h | EM k / model k |
|---|---|---|
| Electric (ring gaps facing) | 0.59 / 1.18 / 1.97 | 1.18 / 1.15 / 1.13 |
| Magnetic (ring folds facing) | 0.98 / 1.97 / 3.14 / 4.92 | 0.78 / 0.72 / 0.65 / 0.43 |
| Mixed (ring arms facing) | 0.59 / 1.18 / 1.93 / 3.15 | 0.95 / 0.91 / 0.82 / 0.77 |
| Hairpin (adjacent hairpins) | 0.98 / 1.97 / 2.76 / 3.71 | 0.42 / 0.42 / 0.34 / 0.24 |

A plain quarter-wave coupled-line coupler in EM (cells `CPL*`) showed the backward coupling (impedance difference) right
and the forward coupling (even/odd velocity difference) 3.4 to 4.4 dB too strong, but scaling the velocity difference alone
did not reproduce the resonator pairs; the error is mostly geometric (corners, open ends, the non-adjacent arm of a
hairpin). So the correction is empirical: `CouplingCorrection` scales the *mutual* capacitance terms (static and air) of
every coupled section by the ratio above for its kind and s/h (piecewise linear in log s/h, held at the ends), leaving each
line's own capacitance alone so resonances do not move. `RingNetwork` classifies each facing pair (gap-gap electric,
fold-fold magnetic, otherwise mixed); `HairpinCircuitModel` applies Hairpin (or Mixed for the ring chain) to the gaps
between resonators. Synthesis, optimisation and the plotted response all use the corrected model.

Check against EM of the filters designed with the old model (exact geometry, lossy model):

| Filter | EM | Model before | Model corrected |
|---|---|---|---|
| QE36 cross-coupled, 3 dB bandwidth | 71.7 MHz | 119.0 | 78.5 |
| QE36 lower zero | 3.478 GHz | 3.434 | 3.50 |
| VH30 hairpin 3 GHz, 3 dB bandwidth | 18.5 MHz | 59.9 | 17.0 |

Consequence: same-orientation hairpins on RO4350B 0.508 mm reach only k of about 0.012 (EM), so a 4-pole hairpin wider
than about 1.4 % is refused with that reason; use the open-loop ring chain (k up to 0.14 to 0.19) for wider bands. The
correction was measured on one substrate; on others it is applied by s/h and should be confirmed with one EM run.

## Loss and resonator Q

The Response tab also plots **S21 with loss** (conductor and dielectric, radiation excluded) and the summary gives the
midband insertion loss with the resonator unloaded Q (conductor and dielectric parts). When the loss is over 3 dB a warning
explains it with Hong eq. 3.62 (loss ~ 4.343 sum(g) / (FBW Qu)) and says which loss dominates. Example: 5.8 GHz, 100 MHz,
4 poles on RO4350B 1.575 mm: Qu about 233 (dielectric-limited), about 5 dB midband loss.

## Accuracy work: EM reference and corner correction

Investigating first-pass errors (rings 4 to 9 % high, hairpins about 5 % high in EM) gave two results:

1. **The 3D EM setup read low.** Single resonators solved with EMerge at *Standard* quality came out 3 to 4 % below
   the same structures at *Accurate* quality and below circuitRF's full-wave planar (method-of-moments) solver; Accurate
   and planar agree within about 1 %. Microstrip cells now get a **planar** EM setup as their main `.cem`
   (`em/<name>.cem`; circuitRF allows one `.cem` per cell, so no second 3D file is staged). Stripline cells keep the 3D EMerge setup. To use 3D EMerge on a microstrip cell, set Quality to Accurate in the placed `.cem`.
   Planar runs take a few minutes per single resonator and 15 to 20 min per 4-pole filter on an M-series MacBook Air.
2. **Corners were the missing model element.** The circuit model treated every 90-degree corner as centreline length.
   Fitting single resonators against planar EM (RO4350B 0.508 mm, W/h = 2.1):

   | Resonator | Planar EM | Old model | With corner correction |
   |---|---|---|---|
   | Ring 2.4 GHz | 2.493 | +4.4 % | -0.3 % |
   | Ring 3.6 GHz | 3.837 | +7.1 % | -0.1 % |
   | Ring 4.8 GHz | 5.258 | +10.1 % | +0.2 % |
   | Ring 5.8 GHz | 6.497 | +12.6 % | +0.3 % |
   | Ring 3.6 GHz, tip gap 0.15 / 0.6 mm | 3.832 / 3.837 | +7.0 / +7.1 % | -0.2 / -0.1 % |
   | Hairpin 2.4 / 3.6 / 5.0 GHz | 2.495 / 3.827 / 5.450 | +4.0 / +6.4 / +9.2 % | within +/-0.6 % (with 0.67 W) |

   Each corner is now an effective electrical shortening of 0.40 W (rings) or 0.67 W (hairpins) per corner
   (`HairpinGeometry.CornerShortening`, `DefaultCornerShortening`). The open-gap capacitance model was confirmed (best-fit
   scale 1.05). The corrections are *effective*: they also absorb tap-junction and open-end effects (a straight tapped
   half-wave line came out 3.3 % *low* in EM, so these effects pull the other way), and they were fitted on one substrate
   only. On other substrates run EM once and use the calibration loop.

### Corner correction on three substrates (planar EM, single resonators)

| Substrate | Er | W/h | Ring corner | Hairpin corner |
|---|---|---|---|---|
| RT/duroid 5880 0.787 mm | 2.2 | 3.0 | 0.30 to 0.32 W | 0.63 to 0.65 W |
| RO4350B 0.508 mm | 3.66 | 2.1 | 0.40 W | 0.67 W |
| RO3010 0.635 mm | 10.2 | 0.88 | 0.52 W | 0.84 W |

Rings follow Gupta's bend model (Hong eq. 4.42) times 0.80 between W/h 2 and 3 and are capped at 0.52 W below (where the
bend model's inductance fails); hairpins are piecewise linear in Er through the three points.

## Known limitations

- Closed-form circuit models: nearest-neighbour coupling only; corners via an EM-fitted effective correction (RO4350B and
  RT/duroid 5880, scaled with Gupta's bend model elsewhere); tap junctions and radiation not modelled.
- Inter-resonator coupling uses an EM-fitted correction measured on RO4350B 0.508 mm only (see above).
- Open-loop ring circuit-model match to the ideal Chebyshev is moderate (RMS error about 0.07, worst in-band RL about 8 dB
  in the circuit model); EM calibration is what makes the result trustworthy.
- The optimiser can push gaps below the fab minimum (0.152 mm in the RO4350B tech); not yet capped.
- Stripline: hairpin reach is lower (homogeneous medium cancels arm coupling); open-loop rings are microstrip only.
- Plugin binary shipped for osx-arm64 only; other platforms need `tools/build-plugin.sh`.

## Licence

MIT (see `circuitrf-plugin/plugin.json`).
