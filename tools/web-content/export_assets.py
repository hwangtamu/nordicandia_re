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
CONTENT_VERSION = "v01-2"

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


# V01: every shipped world kit (themes + special worlds). Names are the bundle stems.
WORLD_BUNDLES = [
    ("world_dungeon_theme_default_assets_all_", "dungeon_default"),
    ("world_dungeon_theme_grass_assets_all_", "dungeon_grass"),
    ("world_dungeon_theme_sand_assets_all_", "dungeon_sand"),
    ("world_dungeon_theme_undead_assets_all_", "dungeon_undead"),
    ("world_dungeon_assets_all_", "dungeon"),
    ("world_town_assets_all_", "town"),
    ("world_tutorial_assets_all_", "tutorial"),
    ("world_golem_assets_all_", "golem"),
    ("world_helheim_assets_all_", "helheim"),
    ("world_niflheim_assets_all_", "niflheim"),
    ("world_odrstrail_assets_all_", "odrstrail"),
    ("world_vanaheim_assets_all_", "vanaheim"),
    ("world_guilddefense_assets_all_", "guilddefense"),
]

# A broad mesh filter for the world kits (dungeon kit patterns + common environment prefixes).
WORLD_MESH_PATTERNS = KIT_PATTERNS + [
    r"^(SM_|MOD_|T_|MOD_|Bld|Building|Rock|Tree|Grass|Cliff|Floor|Wall|Column|Prop|Bridge|Fence|Crate|Barrel|Tent|Statue|Gate|Arch)",
]


def export_bundle_kit(z: zipfile.ZipFile, contains: str, out_name: str) -> dict:
    """Export the meshes and base-colour textures of one world bundle into kit/<out_name>."""
    name = bundle_name(z, contains)
    raw = z.read(name)
    env = UnityPy.load(raw)
    out_dir = OUT / f"kit/{out_name}"
    out_dir.mkdir(parents=True, exist_ok=True)
    pats = [re.compile(p) for p in WORLD_MESH_PATTERNS]
    exported = []
    for obj in env.objects:
        if obj.type.name != "Mesh":
            continue
        mesh = obj.read()
        mname = mesh.m_Name or f"mesh_{obj.path_id}"
        if not any(p.search(mname) for p in pats):
            continue
        try:
            tm = trimesh.load(io.StringIO(mesh.export()), file_type="obj", process=False)
            glb = tm.export(file_type="glb")
        except Exception as exc:  # noqa: BLE001
            print(f"  ! skip {mname}: {exc}")
            continue
        fname = re.sub(r"[^A-Za-z0-9_.-]", "_", mname) + ".glb"
        (out_dir / fname).write_bytes(glb)
        exported.append({
            "name": mname,
            "file": f"kit/{out_name}/{fname}",
            "vertices": int(len(tm.vertices)),
            "triangles": int(len(tm.faces)),
            "bytes": len(glb),
            "texture": texture_for_mesh(mname),
        })
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
        textures.append({"name": tname, "file": f"kit/{out_name}/textures/{fname}", "bytes": len(buf.getvalue())})
    total = sum(m["bytes"] for m in exported)
    print(f"kit {out_name}: {len(exported)} meshes, {len(textures)} textures, {total/1024:.0f} KiB")
    return {
        "bundle": name,
        "bundleSha256": sha256(raw),
        "meshes": sorted(exported, key=lambda m: m["name"]),
        "textures": sorted(textures, key=lambda t: t["name"]),
    }


def export_kits(z: zipfile.ZipFile, manifest: dict) -> None:
    kits = {}
    for contains, out_name in WORLD_BUNDLES:
        try:
            kits[out_name] = export_bundle_kit(z, contains, out_name)
        except SystemExit as exc:
            print(f"  ! kit {out_name}: {exc}")
    manifest["kits"] = kits
    # Back-compat: the client reads manifest.kit for the default dungeon kit.
    if "dungeon_default" in kits:
        manifest["kit"] = kits["dungeon_default"]


def referenced_icon_names() -> set[str]:
    """The image stems gamedata references (Monsters.Image / CharacterRaces.ActorImage)."""
    GAMEDATA = ROOT / "gamedata_decrypted"
    names: set[str] = set()
    for fname, key in (("Monsters.json", "Image"), ("CharacterRaces.json", "ActorImage")):
        path = GAMEDATA / fname
        if not path.exists():
            continue
        for entry in json.loads(path.read_text()):
            data = entry.get("SerializedData")
            if isinstance(data, str):
                data = json.loads(data)
            data = data or entry
            value = data.get(key)
            if value:
                names.add(value.replace("\\", "/").rsplit("/", 1)[-1].rsplit(".", 1)[0])
    return names


def export_avatars(z: zipfile.ZipFile, manifest: dict) -> None:
    out_dir = OUT / "avatars"
    out_dir.mkdir(parents=True, exist_ok=True)
    pats = [re.compile(p) for p in AVATAR_TEXTURE_PATTERNS]
    referenced = referenced_icon_names()
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
            if tname not in referenced and not any(p.search(tname) for p in pats):
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


def write_missing_report(manifest: dict) -> dict:
    """V01: compare the images the gamedata references to the exported sprite set."""
    GAMEDATA = ROOT / "gamedata_decrypted"
    exported = {a["name"]: a for a in manifest.get("avatars", [])}
    referenced: dict[str, set] = {}

    def icon(path: str) -> str:
        return path.replace("\\", "/").rsplit("/", 1)[-1].rsplit(".", 1)[0]

    for fname, key in (("Monsters.json", "Image"), ("CharacterRaces.json", "ActorImage")):
        path = GAMEDATA / fname
        if not path.exists():
            continue
        for entry in json.loads(path.read_text()):
            data = entry.get("SerializedData")
            if isinstance(data, str):
                data = json.loads(data)
            data = data or entry
            value = data.get(key)
            if value:
                referenced.setdefault(icon(value), set()).add(fname)

    missing = {k: sorted(v) for k, v in referenced.items() if k not in exported}
    report = {
        "referenced": len(referenced),
        "exported": len(exported),
        "missing": missing,
        "missingCount": len(missing),
    }
    (OUT / "missing-assets.json").write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
    print(f"missing-assets: {report['missingCount']} of {report['referenced']} referenced icons")
    return report


def main() -> int:
    if not APK.exists():
        print(f"missing {APK}", file=sys.stderr)
        return 1
    OUT.mkdir(parents=True, exist_ok=True)
    manifest: dict = {"contentVersion": CONTENT_VERSION, "sourceApk": APK.name}
    z = zipfile.ZipFile(APK)
    export_kits(z, manifest)
    # Town's static scene needs the Unity Transform hierarchy and static-batch submesh
    # ranges in addition to the individual meshes exported above.
    from export_town_scene import main as export_town_scene
    if export_town_scene() != 0:
        return 1
    town_scene_path = OUT / "kit/town/town_scene.json"
    manifest["townScene"] = json.loads(town_scene_path.read_text())
    export_avatars(z, manifest)
    from export_inventory_art import main as export_inventory_art
    manifest["inventoryArt"] = export_inventory_art()
    manifest["missingAssets"] = write_missing_report(manifest)
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"wrote {OUT / 'manifest.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
