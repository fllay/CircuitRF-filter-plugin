# CircuitRF Filter Plugin (FiltSyn)

An RF/microwave bandpass filter synthesis tool in C# (.NET 9), in the spirit of Nuhertz Filter Solutions, packaged as a
**CircuitRF plugin**. Type a specification, see the circuit-model response and the layout, and place a ready-to-simulate
cell (layout, 3D view, EMerge setup) in a CircuitRF workspace. A desktop app (Avalonia) shares the same core library.

Filter types: **coupled-line (parallel-coupled)**, **hairpin**, and **square open-loop ring chain** (alternating or same
orientation). A closed **EM calibration loop** corrects the systematic gap between the closed-form circuit models and
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

## Installing the plugin in CircuitRF

Add `circuitrf-plugin/` (the folder holding `plugin.json`) under Tools > Plugins, then open Tools > Plugins > Filters >
Filter Synthesis. Validate the manifest with `circuitRF check <folder>` (the checker only accepts paths under the
CircuitRF server root).

## Using it

1. Pick **Medium** (microstrip/stripline) and a **Technology**; Substrate *From technology* reads Er, tan d, thickness
   and copper from the technology's stackup, or choose *Manual*.
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

## Known limitations

- Closed-form circuit models: nearest-neighbour coupling only; bends, tap junctions, radiation not modelled.
- Open-loop ring circuit-model match to the ideal Chebyshev is moderate (RMS error about 0.07, worst in-band RL about 8 dB
  in the circuit model); EM calibration is what makes the result trustworthy.
- The optimiser can push gaps below the fab minimum (0.152 mm in the RO4350B tech); not yet capped.
- Stripline: hairpin reach is lower (homogeneous medium cancels arm coupling); open-loop rings are microstrip only.
- Plugin binary shipped for osx-arm64 only; other platforms need `tools/build-plugin.sh`.

## Licence

MIT (see `circuitrf-plugin/plugin.json`).
