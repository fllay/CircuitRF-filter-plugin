# Filter Synthesis plugin for circuitRF

A `kind: panel` plugin (id `fa.filtsyn`) that puts the FiltSyn hairpin and coupled-line synthesis inside circuitRF:
type a specification, see the circuit-model response and the layout, and **Place cell in workspace** to get a cell
that is ready to simulate: no DXF export.

## Install

Add this folder (`circuitrf-plugin`, the one holding `plugin.json`) to circuitRF's plugins (Tools > Plugins), then
open Tools > Plugins > Filters > Filter Synthesis. Check the manifest any time with
`circuitRF check <this folder>`.

The folder contains an Apple Silicon (osx-arm64) build. Other platforms: `tools/build-plugin.sh <rid>` and add the rid
to `run` in `plugin.json`.

## Using it

1. Pick **Medium** (microstrip or stripline) and a **Technology** from the workspace. With Substrate set to
   *From technology* the plugin reads Er, tan d, thickness and copper from that technology's stackup, so the
   design is made for the same stack the EM solver will see. A stripline uses the two dielectrics around the inner
   copper (b = their sum). Choose *Manual* to type Er, h (or the stripline plane spacing b), tan d and copper
   thickness instead; the panel then ignores the technology's stackup (it is still used for layer names).
2. Set filter type, response, order, centre frequency, bandwidth and ripple. The Response tab overlays the circuit
   model on the ideal prototype; Layout shows the copper; Dimensions lists the sizes.
3. Name the cell and press **Place cell in workspace**. It writes `layout/<name>.clay`, `3d/<name>.c3d` (one lumped
   port per feed, across the dielectric between the strip and the ground below it), `em/<name>.cem` (EMerge,
   driven, f0 +/- EM span x bandwidth). Stripline cells carry ground planes on Top and Bottom Copper and
   a via fence on the edges that have no port; microstrip cells carry the ground plane on Bottom Copper.

## Notes

- The cell's `.cem` refers to its layout by `<name>/3d/<name>.c3d`: if circuitRF places the cell under a different name
  than the one typed here, point the setup at the placed 3D view.
- The cell's TechRef is `../../tech/<technology id>.ctech`; set the `techRef` parameter if the workspace keeps it elsewhere.
- The technology parameter's JSON fields are documented loosely, so the plugin matches several spellings. The first
  request writes what it received to `technology-param.json` in the staging folder; if *From technology* cannot read
  the stackup, send that file along and use *Manual* meanwhile.
- Same limits as the desktop app: closed-form models, no EM inside the plugin; verify with the EM run.
- `tools/plugin_harness.py` drives the binary the way circuitRF does, for testing without circuitRF.
