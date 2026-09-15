#!/usr/bin/env python3
"""Assemble the patched, re-signed APKs into an installable XAPK (normalising timestamps)."""
import os, zipfile

indir = "/local_disk/nordicandia_re"
pdir = os.path.join(indir, "patched")
out = os.path.join(pdir, "Nordicandia_1.9.3_private.xapk")
members = [
    ("com.IterativeStudios.Nordicandia.apk", os.path.join(pdir, "com.IterativeStudios.Nordicandia.apk")),
    ("icon.png", os.path.join(indir, "icon.png")),
    ("config.armeabi_v7a.apk", os.path.join(pdir, "config.armeabi_v7a.apk")),
    ("UnityDataAssetPack.apk", os.path.join(pdir, "UnityDataAssetPack.apk")),
    ("manifest.json", os.path.join(indir, "manifest.json")),
]
with zipfile.ZipFile(out, "w", zipfile.ZIP_STORED) as z:
    for name, path in members:
        if not os.path.exists(path):
            print("missing", path); continue
        with open(path, "rb") as f:
            data = f.read()
        zi = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
        zi.compress_type = zipfile.ZIP_STORED
        z.writestr(zi, data)
print("wrote", out, os.path.getsize(out), "bytes")
