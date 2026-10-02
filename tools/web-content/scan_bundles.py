#!/usr/bin/env python3
"""M0 asset inventory: classify every Addressables bundle in the Unity asset pack.

Outputs tmp/web-m0/bundle-inventory.json with per-bundle asset type counts and
the names of model / animation / avatar / skinned-mesh / prefab-relevant assets,
so we can decide which bundles to convert to glTF for the web client.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/scan_bundles.py
"""
from __future__ import annotations

import json
import pathlib
import sys
import zipfile
import collections

import UnityPy

ROOT = pathlib.Path(__file__).resolve().parents[2]
APK = ROOT / "dist/android-arm64-src/UnityDataAssetPack.apk"
OUT = ROOT / "tmp/web-m0/bundle-inventory.json"

INTERESTING = {
    "Mesh",
    "SkinnedMeshRenderer",
    "Animator",
    "AnimationClip",
    "Avatar",
    "GameObject",
    "Material",
    "Texture2D",
    "AudioClip",
    "MonoBehaviour",
}


def name_of(obj) -> str:
    try:
        data = obj.read()
    except Exception as exc:  # noqa: BLE001
        return f"<read-error {x(exc)}>"
    return str(getattr(data, "m_Name", ""))


def x(exc: BaseException) -> str:
    return str(exc).replace("\n", " ")[:120]


def scan_bundle(raw: bytes) -> dict:
    env = UnityPy.load(raw)
    counts: collections.Counter[str] = collections.Counter()
    names: dict[str, list[dict]] = collections.defaultdict(list)
    for obj in env.objects:
        t = obj.type.name
        counts[t] += 1
        if t in INTERESTING:
            names[t].append({"id": obj.path_id, "name": name_of(obj)})
    return {"counts": dict(counts), "assets": {k: v for k, v in names.items()}}


def main() -> int:
    if not APK.exists():
        print(f"missing {APK}", file=sys.stderr)
        return 1
    z = zipfile.ZipFile(APK)
    bundle_names = sorted(n for n in z.namelist() if n.endswith(".bundle"))
    report = []
    for i, name in enumerate(bundle_names, 1):
        if not name.endswith(".bundle"):
            continue
        print(f"[{i}/{len(bundle_names)}] {name}", flush=True)
        # Save a per-bundle cache so re-runs of the converter are cheap.
        scan_path = ROOT / "tmp/web-m0/bundle-scans" / (name.rsplit("/", 1)[-1] + ".json")
        scan_path.parent.mkdir(parents=True, exist_ok=True)
        if scan_path.exists():
            entry = json.loads(scan_path.read_text())
        else:
            try:
                entry = scan_bundle(z.read(name))
            except Exception as exc:  # noqa: BLE001
                entry = {"error": x(exc)}
            scan_path.write_text(json.dumps(entry, indent=2))
        entry["bundle"] = name
        report.append(entry)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(report, indent=2))
    print(f"wrote {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
