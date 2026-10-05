#!/usr/bin/env python3
"""Export original item sprites used by the web inventory.

The mapping is keyed by Items.json IntegerId, not the Sprite basename: the
source bundle contains many unrelated images named "1", "2", etc.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import zipfile

import UnityPy
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
APK = ROOT / "dist/android-arm64-src/UnityDataAssetPack.apk"
ITEMS = ROOT / "gamedata_decrypted/Items.json"
OUT = ROOT / "web/public/assets/items"
UI_OUT = ROOT / "web/public/assets/inventory-ui"


def matching_bundle(archive: zipfile.ZipFile, marker: str) -> str:
    return next(name for name in archive.namelist() if marker in name and name.endswith(".bundle"))


def save_sprite(sprite, destination: Path, size: int = 160) -> None:
    image = sprite.read().image.convert("RGBA")
    image.thumbnail((size, size), Image.Resampling.LANCZOS)
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination, format="PNG", compress_level=6)


def main() -> dict:
    rows = json.loads(ITEMS.read_text())
    with zipfile.ZipFile(APK) as archive:
        bundle_name = matching_bundle(archive, "textures_items_assets_all_")
        raw = archive.read(bundle_name)
        env = UnityPy.load(raw)
        by_path = {path.lower(): obj for path, obj in env.container.items()
                   if obj.type.name == "Sprite"}
        paths: dict[str, str] = {}
        missing: list[int] = []
        image_rows = [row for row in rows if row.get("SerializedData", {}).get("Image")]
        for row in image_rows:
            image = row["SerializedData"]["Image"]
            key = "assets/addressables/gameshared/" + image.replace("\\", "/").lstrip("/")
            sprite = by_path.get(key.lower())
            integer_id = row["IntegerId"]
            if sprite is None:
                missing.append(integer_id)
                continue
            filename = f"{integer_id}.png"
            save_sprite(sprite, OUT / filename)
            paths[str(integer_id)] = f"/assets/items/{filename}"

        packed_name = matching_bundle(archive, "packedassets_assets_all_")
        packed_raw = archive.read(packed_name)
        packed = UnityPy.load(packed_raw)
        ui_names = {"Frame_Background", "Frame_InnerFrame", "Frame_Top_Gold",
                    "GlowingBorder_Yellow", "UnitFrame_Avatar2",
                    "Badge_warrior", "Badge_paladin", "Badge_assassin", "Badge_barbarian",
                    "Badge_hunter", "Badge_mage", "Badge_necro", "Badge_priest"}
        ui_paths: dict[str, str] = {}
        for obj in packed.objects:
            if obj.type.name != "Sprite":
                continue
            sprite = obj.read()
            if sprite.m_Name not in ui_names:
                continue
            filename = f"{sprite.m_Name}.png"
            save_sprite(obj, UI_OUT / filename, 512)
            ui_paths[sprite.m_Name] = f"/assets/inventory-ui/{filename}"

    source = "Android 1.9.3 UnityDataAssetPack.apk"
    item_manifest = {
        "source": f"{source} / Textures/Items",
        "bundleSha256": hashlib.sha256(raw).hexdigest(),
        "itemCount": len(paths),
        "itemsWithImage": len(image_rows),
        "items": paths,
        "missing": missing,
    }
    (OUT / "manifest.json").write_text(json.dumps(item_manifest, indent=2) + "\n")
    (UI_OUT / "manifest.json").write_text(json.dumps({
        "source": f"{source} / packed assets",
        "bundleSha256": hashlib.sha256(packed_raw).hexdigest(),
        "sprites": ui_paths,
    }, indent=2) + "\n")

    summary = {
        "source": source,
        "itemManifest": "items/manifest.json",
        "itemCount": len(paths),
        "itemsWithImage": len(image_rows),
        "missingItemIds": missing,
        "uiManifest": "inventory-ui/manifest.json",
        "uiSpriteCount": len(ui_paths),
    }
    print(f"Exported {len(paths)}/{len(image_rows)} original item sprites; missing IDs: {missing}")
    return summary


if __name__ == "__main__":
    main()
