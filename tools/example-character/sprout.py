# The example character, "Sprout": a round bean with a two-leaf sprout on top, built only
# from ellipsoids so it needs nothing but numpy. Writes a glTF binary (.glb).
# usage: python3 sprout.py sprout.glb
#
# This is the kind of script you can ask an AI assistant to write for your own original
# character: describe the shapes and colours, keep feet at y=0 and the front facing +z.
import json, struct, sys
import numpy as np

out = sys.argv[1] if len(sys.argv) > 1 else "sprout.glb"

def ellipsoid(c, r, nlat=28, nlon=40, rot=None):
    ph = np.linspace(-np.pi / 2, np.pi / 2, nlat); th = np.linspace(-np.pi, np.pi, nlon)
    T, P = np.meshgrid(th, ph)
    d = np.stack([np.cos(P) * np.sin(T), np.sin(P), np.cos(P) * np.cos(T)], -1).reshape(-1, 3)
    pos = d * r; nrm = d / r
    if rot is not None: pos = pos @ rot.T; nrm = nrm @ rot.T
    nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
    tris = []
    for i in range(nlat - 1):
        for j in range(nlon - 1):
            a, b, cc, dd = i * nlon + j, i * nlon + j + 1, (i + 1) * nlon + j + 1, (i + 1) * nlon + j
            tris += [(a, cc, dd), (a, b, cc)]
    return pos + c, nrm, np.array(tris)

def rot_z(a):
    c, s = np.cos(a), np.sin(a); return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1.0]])
def rot_x(a):
    c, s = np.cos(a), np.sin(a); return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])

BODY_C, BODY_R = np.array([0, 0.27, 0]), np.array([0.25, 0.26, 0.22])
def on_body(x, y, lift=0.0):
    """a point on the front of the body at height y and sideways x"""
    u = 1 - (x / BODY_R[0]) ** 2 - ((y - BODY_C[1]) / BODY_R[1]) ** 2
    return np.array([x, y, BODY_R[2] * np.sqrt(max(u, 0)) + lift])

parts = {   # name: (colour sRGB, roughness, [ellipsoids])
    "body":   ((246, 238, 220), 0.8, [ellipsoid(BODY_C, BODY_R)]),
    "feet":   ((236, 222, 196), 0.8, [ellipsoid(np.array([s * 0.09, 0.03, 0.05]), np.array([0.065, 0.035, 0.075])) for s in (-1, 1)]),
    "arms":   ((246, 238, 220), 0.8, [ellipsoid(np.array([s * 0.235, 0.19, 0.05]), np.array([0.05, 0.04, 0.045]), rot=rot_z(s * 0.5)) for s in (-1, 1)]),
    "eyes":   ((40, 34, 32), 0.4, [ellipsoid(on_body(s * 0.075, 0.30, -0.006), np.array([0.024, 0.032, 0.014])) for s in (-1, 1)]),
    "shine":  ((255, 255, 255), 0.3, [ellipsoid(on_body(s * 0.075 - 0.008, 0.312, 0.004), np.array([0.008, 0.009, 0.004])) for s in (-1, 1)]),
    "cheeks": ((245, 170, 175), 0.9, [ellipsoid(on_body(s * 0.145, 0.245, -0.008), np.array([0.04, 0.022, 0.012])) for s in (-1, 1)]),
    "mouth":  ((90, 50, 45), 0.6, [ellipsoid(on_body(0, 0.25, -0.004), np.array([0.016, 0.008, 0.008]))]),
    "stem":   ((110, 170, 90), 0.7, [ellipsoid(np.array([0, 0.55, -0.01]), np.array([0.012, 0.05, 0.012]))]),
    "leaves": ((126, 196, 104), 0.7, [ellipsoid(np.array([s * 0.06, 0.6, -0.01]), np.array([0.065, 0.02, 0.035]), rot=rot_z(s * 0.45)) for s in (-1, 1)]),
}

def lin(c):
    c = np.array(c) / 255.0; return (np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)).tolist() + [1.0]

blobs, views, accs, meshes, mats, nodes = [], [], [], [], [], []
off = 0
def add(arr, target, kind, comp, mm=False):
    global off
    b = arr.tobytes(); pad = (4 - len(b) % 4) % 4
    blobs.append(b + b"\0" * pad)
    views.append({"buffer": 0, "byteOffset": off, "byteLength": len(b), "target": target}); off += len(b) + pad
    a = {"bufferView": len(views) - 1, "componentType": comp, "count": len(arr), "type": kind}
    if mm: a["min"] = arr.min(0).tolist(); a["max"] = arr.max(0).tolist()
    accs.append(a); return len(accs) - 1

for name, (col, rough, shapes) in parts.items():
    P, N, F, o = [], [], [], 0
    for p, n, f in shapes: P.append(p); N.append(n); F.append(f + o); o += len(p)
    P = np.concatenate(P).astype(np.float32); N = np.concatenate(N).astype(np.float32); F = np.concatenate(F).astype(np.uint32)
    mats.append({"name": name, "pbrMetallicRoughness": {"baseColorFactor": lin(col), "metallicFactor": 0, "roughnessFactor": rough}})
    meshes.append({"name": name, "primitives": [{"attributes": {"POSITION": add(P, 34962, "VEC3", 5126, True), "NORMAL": add(N, 34962, "VEC3", 5126)},
                                                  "indices": add(F.ravel(), 34963, "SCALAR", 5125), "material": len(mats) - 1}]})
    nodes.append({"name": name, "mesh": len(meshes) - 1})

gltf = {"asset": {"version": "2.0", "generator": "tabpet sprout.py"}, "scene": 0,
        "scenes": [{"nodes": [len(nodes)]}], "nodes": nodes + [{"name": "sprout", "children": list(range(len(nodes)))}],
        "meshes": meshes, "materials": mats, "accessors": accs, "bufferViews": views, "buffers": [{"byteLength": off}]}
js = json.dumps(gltf, separators=(",", ":")).encode(); js += b" " * ((4 - len(js) % 4) % 4)
binb = b"".join(blobs)
with open(out, "wb") as f:
    f.write(struct.pack("<III", 0x46546C67, 2, 28 + len(js) + len(binb)))
    f.write(struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(binb), 0x004E4942) + binb)
print("wrote", out)
