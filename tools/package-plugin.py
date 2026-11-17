#!/usr/bin/env python3
"""Package the CircuitRF plugin folder into a zip.

usage: package-plugin.py OUT.zip RID=EXE_PATH [RID=EXE_PATH ...]

The zip holds plugin.json (its "run" map rewritten to exactly the given rids), README.md and bin/<rid>/<exe>.
Unix executables keep their mode bits so the plugin starts after unzipping."""
import json, os, stat, sys, zipfile

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "circuitrf-plugin")
out, items = sys.argv[1], [a.split("=", 1) for a in sys.argv[2:]]
if not items: sys.exit(__doc__)
manifest = json.load(open(os.path.join(root, "plugin.json")))
manifest["run"] = {rid: {"exe": f"bin/{rid}/{os.path.basename(exe)}"} for rid, exe in items}
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("filtsyn-plugin/plugin.json", json.dumps(manifest, indent=2) + "\n")
    z.write(os.path.join(root, "README.md"), "filtsyn-plugin/README.md")
    for rid, exe in items:
        info = zipfile.ZipInfo(f"filtsyn-plugin/bin/{rid}/{os.path.basename(exe)}")
        info.external_attr = (stat.S_IFREG | 0o755) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, open(exe, "rb").read())
print("wrote", out, [r for r, _ in items])
