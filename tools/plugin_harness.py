"""Minimal circuitRF plugin host for testing filtsyn-plugin without circuitRF.
Usage: python3 plugin_harness.py <plugin exe> <staging dir> [commit]
Speaks the framed protocol: [u32 jsonLen][u32 binLen][json][int64 payload], little endian."""
import json, struct, subprocess, sys, os, time
OPT = (os.environ.get("OPT", "1") == "1")

def send(p, obj):
    b = json.dumps(obj).encode()
    p.stdin.write(struct.pack("<II", len(b), 0) + b); p.stdin.flush()

def recv(p):
    h = p.stdout.read(8)
    if len(h) < 8: return None, b""
    jl, bl = struct.unpack("<II", h)
    j = json.loads(p.stdout.read(jl)); b = p.stdout.read(bl)
    return j, b

def fake_host(j, staging):
    """Answers a simulate call with a synthetic 2-port on the analysis' own grid (protocol test only)."""
    import re, math
    text = open(j["cnl"]).read()
    m = re.search(r"start=([\d.eE+-]+) GHz stop=([\d.eE+-]+) GHz npts=(\d+)", text)
    a, b, n = float(m[1]), float(m[2]), int(m[3])
    out = os.path.join(staging, "crf_check.s2p")
    with open(out, "w") as f:
        f.write("# GHz S RI R 50\n")
        for i in range(n):
            fr = a + (b - a) * i / (n - 1)
            f.write(f"{fr} 0.5 0.1 0.7 0.0 0.7 0.0 0.5 0.1\n")
    return {"type": "hostReply", "id": j["id"], "touchstone": out, "result": os.path.join(staging, "x.npy"), "cubes": ["S"], "messages": []}

def run(exe, staging, params, commit=False):
    os.makedirs(staging, exist_ok=True)
    p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    send(p, {"type": "hello", "id": 1, "apiVersion": 1, "circuitRF": "1.8.3", "platform": "linux", "staging": staging,
             "workspace": staging, "context": {}, "mode": "interactive"})
    print("hello ->", recv(p)[0])
    send(p, {"type": "update", "id": 2, "params": params})
    t = time.time()
    while True:
        j, b = recv(p)
        if j is None: print("EOF", p.stderr.read().decode()[-2000:]); return
        if j["type"] == "progress": print("  progress", j["text"]); continue
        if j["type"] == "host":
            print("  host call:", {k: v for k, v in j.items() if k != "type"})
            send(p, fake_host(j, staging)); continue
        break
    print(f"update reply in {time.time()-t:.1f}s: type={j['type']} id={j['id']} bin={len(b)//8} int64")
    print(" messages:", j.get("messages"))
    print(" summary :", j.get("readouts", {}).get("summary"))
    views = j.get("views", {})
    print(" views   :", list(views))
    if "artwork" in views:
        a = views["artwork"]; print("  shapes:", len(a["shapes"]), "ports:", a["ports"])
    json.dump(j, open(os.path.join(staging, "reply.json"), "w"))
    em = os.environ.get("EMFILE")
    if em:   # calibrate from an EM result, twice (the second call must keep working on the updated factors)
        for i in range(2):
            params["emResult"] = em
            send(p, {"type": "action", "id": 10 + i, "name": "calibrate", "params": params})
            while True:
                j, b = recv(p)
                if j is None: print("EOF"); return
                if j["type"] == "progress": continue
                if j["type"] == "host": send(p, fake_host(j, staging)); continue
                break
            print(f" calibrate #{i+1}: set={j.get('set')} messages={[m['text'] for m in j.get('messages', [])][:2]}")
            for k, v in (j.get("set") or {}).items(): params[k] = v
        send(p, {"type": "action", "id": 20, "name": "resetCalibration", "params": params})
        while True:
            j, b = recv(p)
            if j["type"] == "host": send(p, fake_host(j, staging)); continue
            if j["type"] != "progress": break
        print(" reset: set=", j.get("set"))
    if commit:
        send(p, {"type": "commit", "id": 3, "params": params})
        while True:
            j, b = recv(p)
            if j["type"] == "progress": continue
            break
        print("commit ->", json.dumps(j)[:400])
    send(p, {"type": "bye"})
    try: p.wait(timeout=3)
    except Exception: p.kill()
    err = p.stderr.read().decode()
    if err.strip(): print("stderr:", err[-1500:])

if __name__ == "__main__":
    exe, staging = sys.argv[1], sys.argv[2]
    params = {"filter": "Coupled-line", "response": "Butterworth", "order": 4,
              "f0": {"value": 3.5e9, "unit": "Hz", "text": "3.5 GHz"}, "bw": {"value": 200e6, "unit": "Hz", "text": "200 MHz"},
              "ripple": {"value": 0.1, "unit": "dB", "text": "0.1 dB"}, "medium": "Stripline",
              "technology": {"id": "stripline_RT5880_2x31mil_1oz", "dbuPerMicron": 1000},
              "substrateSource": "Manual", "er": 2.2, "h": {"value": 1.5748e-3, "unit": "m"}, "tand": 0.0009,
              "copper": {"value": 35e-6, "unit": "m"}, "cellName": os.environ.get("CELL", "FT_TEST"), "emSpan": 1.5, "emPoints": 81}
    if len(sys.argv) > 3 and sys.argv[3] == "crf":
        params.update({"filter": "Coupled-line", "medium": "Microstrip", "response": "Chebyshev", "order": 4,
                       "f0": {"value": float(os.environ.get("F0_MHZ", "3600"))*1e6, "unit": "Hz"}, "bw": {"value": float(os.environ.get("BW_MHZ", "200"))*1e6, "unit": "Hz"}, "er": 3.66,
                       "h": {"value": 0.508e-3, "unit": "m"}, "tand": 0.0037, "crfCheck": True, "optimize": OPT,
                       "technology": {"id": "PCB 2-Layer RO4350B (20mil, 1oz)", "dbuPerMicron": 1000}})
        os.makedirs(os.path.join(staging, "tech"), exist_ok=True)
        open(os.path.join(staging, "tech", "pcb-2layer_RO4350B_20mil_1oz.ctech"), "w").write('{"Name": "PCB 2-Layer RO4350B (20mil, 1oz)"}')
        params["techRef"] = "../../tech/pcb-2layer_RO4350B_20mil_1oz.ctech"
    if len(sys.argv) > 3 and sys.argv[3] == "olr":
        params.update({"filter": "Open-loop ring", "orientation": os.environ.get("ORIENT", "Alternating"),
                       "tipGap": {"value": 0.3e-3, "unit": "m"}, "medium": "Microstrip", "response": "Chebyshev", "order": 4,
                       "f0": {"value": float(os.environ.get("F0_MHZ", "3600"))*1e6, "unit": "Hz"},
                       "bw": {"value": float(os.environ.get("BW_MHZ", "150"))*1e6, "unit": "Hz"}, "er": 3.66,
                       "h": {"value": 0.508e-3, "unit": "m"}, "tand": 0.0037, "optimize": OPT, "substrateSource": "Manual",
                       "f0Corr": float(os.environ.get("F0CORR","1")), "bwCorr": float(os.environ.get("BWCORR","1")),
                       "emSpan": 3.0, "technology": {"id": "PCB 2-Layer RO4350B (20mil, 1oz)", "dbuPerMicron": 1000}, "techRef": "../../tech/pcb-2layer_RO4350B_20mil_1oz.ctech"})
        os.makedirs(os.path.join(staging, "tech"), exist_ok=True)
        open(os.path.join(staging, "tech", "pcb-2layer_RO4350B_20mil_1oz.ctech"), "w").write('{"Name": "PCB 2-Layer RO4350B (20mil, 1oz)"}')
    if len(sys.argv) > 3 and sys.argv[3] == "hairpin": params["filter"] = "Hairpin"; params["bw"]["value"] = 60e6
    run(exe, staging, params, commit=True)
