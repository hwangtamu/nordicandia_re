#!/usr/bin/env python3
"""Export the original power icons used by the web class skill pools."""
from __future__ import annotations

import json
from pathlib import Path
import re
import zipfile

import UnityPy
from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
APK = ROOT / "dist/android-arm64-src/UnityDataAssetPack.apk"
POOLS = ROOT / "server/Nordicandia.Server/GameData/power_pools.json"
OUT = ROOT / "web/public/assets/power-icons"


def main() -> None:
    pools = json.loads(POOLS.read_text())
    names = {row["icon"] for pool in pools.values()
             for kind in ("active", "passive") for row in pool[kind]}
    if any(not re.fullmatch(r"[A-Za-z0-9_]+", name) for name in names):
        raise ValueError("Power icon name cannot be used as a filename")

    with zipfile.ZipFile(APK) as archive:
        bundle_name = next(name for name in archive.namelist()
                           if "textures_powers_assets_all_" in name and name.endswith(".bundle"))
        env = UnityPy.load(archive.read(bundle_name))
        sprites = {obj.read().m_Name: obj for obj in env.objects
                   if obj.type.name == "Sprite"}
        missing = sorted(names - sprites.keys())
        if missing:
            raise ValueError(f"Missing power sprites: {missing}")
        OUT.mkdir(parents=True, exist_ok=True)
        for name in sorted(names):
            image = sprites[name].read().image.convert("RGBA")
            image.thumbnail((128, 128), Image.Resampling.LANCZOS)
            image.save(OUT / f"{name}.png", format="PNG", compress_level=6)
    (OUT / "manifest.json").write_text(json.dumps({
        "source": "Android textures_powers_assets_all bundle",
        "count": len(names),
        "icons": {name: f"/assets/power-icons/{name}.png" for name in sorted(names)},
    }, indent=2) + "\n")
    print(f"Exported {len(names)} original power icons")


if __name__ == "__main__":
    main()
