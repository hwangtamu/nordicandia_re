#!/usr/bin/env python3
"""M0 content export: turn shipped Unity assets into web-ready GLB + PNG files.

Findings that drive this script (see docs/web/M0_ASSET_AUDIT.md):

* The shipped Addressables bundles contain the 3D *environment* kit (floors,
  walls, columns, props, doors) as `Mesh` objects.
* They do **not** contain rigged player/monster characters. Characters and
  monsters are 2D avatar sprites (`Textures/Avatars/...`) referenced from
  `gamedata_decrypted/{CharacterRaces,Monsters}.json`.
* Therefore the web client renders a 3D environment kit + billboarded avatar
  sprites. This script exports both.

Outputs (all under web/public/assets):
  kit/dungeon_default/<mesh>.glb   environment meshes (OBJ -> glTF)
  avatars/<texture>.png            character/monster/icon sprites
  manifest.json                    inventory + source hashes + content version

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/export_assets.py
"""
from __future__ import annotations

import hashlib
import io
import json
import pathlib
import re
import sys
import zipfile

import UnityPy
import trimesh

ROOT = pathlib.Path(__file__).resolve().parents[2]
APK = ROOT / "dist/android-arm64-src/UnityDataAssetPack.apk"
OUT = ROOT / "web/public/assets"

# Content-version tag. Bump when the export rules change so clients can invalidate.
CONTENT_VERSION = "m0-1"

DUNGEON_BUNDLE = "world_dungeon_theme_default_assets_all_"
# Meshes that make up a usable room kit. Names come from the bundle inventory.
KIT_PATTERNS = [
    r"^Floor_Slab",
    r"^Flor_Slab",
    r"^MOD_Floor",
    r"^MOD_Wall",
    r"^MOD_Column",
    r"^MOD_Railing",
    r"^SM_PROP_(torch|brazier|planks|debris|wallshelf)",
    r"^Door$",
    r"^DoorSlab[LR]$",
    r"^DoorOneWay$",
    r"^CliffEdge_Straight$",
]

AVATAR_BUNDLES = ["packedassets_assets_all_"]
AVATAR_TEXTURE_PATTERNS = [
    r"^Human_\d+_nobg",
    r"^Human2$",
    r"^Race[A-Z]\w+$",
    r"^Monsters_\d+_nobg",
    r"^Mobs_\w+",
    r"^Orcwarrior_\w+",
    r"^Warrior_\d+$",
    r"^Badge_warrior$",
    r"^MonstersAvatarIcons_\d+$",
]


DUNGEON_TEXTURE_PATTERNS = [r"_BC($|_)", r"Base_Color"]


def texture_for_mesh(mesh_name: str) -> str:
    """Provisional mesh -> base-colour texture mapping for the dungeon kit."""
    n = mesh_name.lower()
    if "floor" in n or "flor" in n or "cliff" in n:
        return "Stone_Base_Color"
    if "wall" in n or "column" in n or "railing" in n:
        return "T_ENV_MOD_Wall_01_v3_BC_Blue"
    if "planks" in n or "door" in n:
        return "T_wood_planks_dungeon_04_BC"
    if "brazier" in n or "torch" in n or "metal" in n:
        return "T_metal_dungeon_01_BC"
    if "debris" in n or "wallshelf" in n or "orevein" in n:
        return "T_PROP_orevein_dungeon_BC"
    return "Stone_Base_Color"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def bundle_name(z: zipfile.ZipFile, contains: str) -> str:
    for n in z.namelist():
        if contains in n and n.endswith(".bundle"):
            return n
    raise SystemExit(f"no bundle matching {contains!r}")


def export_kit(z: zipfile.ZipFile, manifest: dict) -> None:
    name = bundle_name(z, DUNGEON_BUNDLE)
    raw = z.read(name)
    env = UnityPy.load(raw)
    out_dir = OUT / "kit/dungeon_default"
    out_dir.mkdir(parents=True, exist_ok=True)
    pats = [re.compile(p) for p in KIT_PATTERNS]
    exported = []
    for obj in env.objects:
        if obj.type.name != "Mesh":
            continue
        mesh = obj.read()
        mname = mesh.m_Name or f"mesh_{obj.path_id}"
        if not any(p.search(mname) for p in pats):
            continue
        try:
            obj_text = mesh.export()
            tm = trimesh.load(io.StringIO(obj_text), file_type="obj", process=False)
            glb = tm.export(file_type="glb")
        except Exception as exc:  # noqa: BLE001
            print(f"  ! skip {mname}: {exc}")
            continue
        fname = re.sub(r"[^A-Za-z0-9_.-]", "_", mname) + ".glb"
        (out_dir / fname).write_bytes(glb)
        exported.append({
            "name": mname,
            "file": f"kit/dungeon_default/{fname}",
            "vertices": int(len(tm.vertices)),
            "triangles": int(len(tm.faces)),
            "bytes": len(glb),
        })
    # Base-colour textures for the kit.
    tex_dir = out_dir / "textures"
    tex_dir.mkdir(parents=True, exist_ok=True)
    tex_pats = [re.compile(p) for p in DUNGEON_TEXTURE_PATTERNS]
    textures = []
    for obj in env.objects:
        if obj.type.name != "Texture2D":
            continue
        tex = obj.read()
        tname = tex.m_Name or ""
        if not any(p.search(tname) for p in tex_pats):
            continue
        try:
            image = tex.image
            if image is None:
                continue
            buf = io.BytesIO()
            image.save(buf, format="PNG")
        except Exception as exc:  # noqa: BLE001
            print(f"  ! skip tex {tname}: {exc}")
            continue
        fname = re.sub(r"[^A-Za-z0-9_.-]", "_", tname) + ".png"
        (tex_dir / fname).write_bytes(buf.getvalue())
        textures.append({"name": tname, "file": f"kit/dungeon_default/textures/{fname}", "bytes": len(buf.getvalue())})
    for mesh in exported:
        mesh["texture"] = texture_for_mesh(mesh["name"])
    manifest["kit"] = {
        "bundle": name,
        "bundleSha256": sha256(raw),
        "meshes": sorted(exported, key=lambda m: m["name"]),
        "textures": sorted(textures, key=lambda t: t["name"]),
    }
    total = sum(m["bytes"] for m in exported)
    print(f"kit: {len(exported)} meshes, {len(textures)} textures, {total/1024:.0f} KiB")


def export_avatars(z: zipfile.ZipFile, manifest: dict) -> None:
    out_dir = OUT / "avatars"
    out_dir.mkdir(parents=True, exist_ok=True)
    pats = [re.compile(p) for p in AVATAR_TEXTURE_PATTERNS]
    exported: dict[str, dict] = {}
    for bundle in AVATAR_BUNDLES:
        name = bundle_name(z, bundle)
        raw = z.read(name)
        env = UnityPy.load(raw)
        for obj in env.objects:
            if obj.type.name != "Texture2D":
                continue
            tex = obj.read()
            tname = tex.m_Name or f"tex_{obj.path_id}"
            if not any(p.search(tname) for p in pats):
                continue
            if tname in exported:
                continue
            try:
                image = tex.image
                if image is None:
                    continue
                buf = io.BytesIO()
                image.save(buf, format="PNG")
            except Exception as exc:  # noqa: BLE001
                print(f"  ! skip tex {tname}: {exc}")
                continue
            fname = re.sub(r"[^A-Za-z0-9_.-]", "_", tname) + ".png"
            (out_dir / fname).write_bytes(buf.getvalue())
            exported[tname] = {
                "name": tname,
                "file": f"avatars/{fname}",
                "w": image.width,
                "h": image.height,
                "bytes": len(buf.getvalue()),
            }
    manifest["avatars"] = sorted(exported.values(), key=lambda a: a["name"])
    print(f"avatars: {len(exported)} textures")


def main() -> int:
    if not APK.exists():
        print(f"missing {APK}", file=sys.stderr)
        return 1
    OUT.mkdir(parents=True, exist_ok=True)
    manifest: dict = {"contentVersion": CONTENT_VERSION, "sourceApk": APK.name}
    z = zipfile.ZipFile(APK)
    export_kit(z, manifest)
    export_avatars(z, manifest)
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"wrote {OUT / 'manifest.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
