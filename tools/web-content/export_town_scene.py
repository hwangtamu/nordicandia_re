#!/usr/bin/env python3
"""Export the Android 1.9.3 Town scene and its NPC spawn locations for the web hub.

The Town scene bundle contains Unity's static-batched meshes. This exporter selects each
MeshRenderer's StaticBatchInfo submesh before applying its transform, instead of exporting
the entire combined mesh once per renderer.

Outputs:
  web/public/assets/kit/town/town_scene.glb
  web/public/assets/kit/town/town_scene.json

Run with:
  .tools/web-assets-venv/bin/python tools/web-content/export_town_scene.py
"""
from __future__ import annotations

import hashlib
import io
import json
import math
import pathlib
import re
import sys
import zipfile

import numpy as np
import trimesh
import UnityPy

ROOT = pathlib.Path(__file__).resolve().parents[2]
APK = ROOT / "dist/android-arm64-src/UnityDataAssetPack.apk"
OUT = ROOT / "web/public/assets/kit/town"
TOWN_SCALE = 0.17
PLAYER_SPAWN_NAME = "SpawnZone_Player "

# These Town bundle dependencies supply the original mesh/material/texture pointers.
DEPENDENCIES = (
    "world_town_scenes_all_",
    "world_town_assets_all_",
    "world_golem_assets_all_",
    "packedassets_assets_all_",
    "textures_items_assets_all_",
    "3508883b0e308d3d7312cd700aabe898_unitybuiltinassets_",
    "3508883b0e308d3d7312cd700aabe898_monoscripts_",
)


def bundle_entry(archive: zipfile.ZipFile, prefix: str) -> str:
    matches = [name for name in archive.namelist() if prefix in name and name.endswith(".bundle")]
    if len(matches) != 1:
        raise RuntimeError(f"expected one bundle containing {prefix!r}, found {matches}")
    return matches[0]


def vector3(value) -> tuple[float, float, float]:
    return float(value.x), float(value.y), float(value.z)


def transform_matrix(transform, cache: dict[int, np.ndarray]) -> np.ndarray:
    if transform.path_id in cache:
        return cache[transform.path_id]
    data = transform.read()
    px, py, pz = vector3(data.m_LocalPosition)
    sx, sy, sz = vector3(data.m_LocalScale)
    q = data.m_LocalRotation
    x, y, z, w = float(q.x), float(q.y), float(q.z), float(q.w)
    rotation = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0],
        [0, 0, 0, 1],
    ], dtype=float)
    translation = np.eye(4)
    translation[:3, 3] = [px, py, pz]
    scale = np.diag([sx, sy, sz, 1.0])
    matrix = translation @ rotation @ scale
    if data.m_Father:
        parent = data.m_Father.deref()
        if parent is not None:
            matrix = transform_matrix(parent, cache) @ matrix
    cache[transform.path_id] = matrix
    return matrix


def parse_obj_groups(text: str) -> tuple[list[str], list[list[str]]]:
    """Keep global OBJ vertex/UV/normal tables and separate face lists by g group."""
    shared: list[str] = []
    groups: list[list[str]] = []
    current: list[str] | None = None
    for line in text.splitlines():
        if line.startswith("g "):
            if current is not None:
                groups.append(current)
            current = []
        elif line.startswith(("v ", "vt ", "vn ")):
            shared.append(line)
        elif line.startswith("f ") and current is not None:
            current.append(line)
    if current is not None:
        groups.append(current)
    return shared, groups


def geometry_for_submesh(mesh, submesh: int) -> trimesh.Trimesh | None:
    shared, groups = parse_obj_groups(mesh.export())
    if submesh < 0 or submesh >= len(groups) or not groups[submesh]:
        return None
    text = "\n".join(shared + groups[submesh]) + "\n"
    try:
        result = trimesh.load(io.StringIO(text), file_type="obj", process=False)
    except Exception as exc:  # noqa: BLE001
        print(f"  ! skip submesh {mesh.m_Name}[{submesh}]: {exc}")
        return None
    if not isinstance(result, trimesh.Trimesh) or len(result.faces) == 0:
        return None
    return result


def material_for_renderer(renderer, material_index: int, texture_cache: dict[int, object]):
    materials = renderer.read().m_Materials
    if not materials:
        return trimesh.visual.material.PBRMaterial(baseColorFactor=[160, 160, 160, 255])
    pointer = materials[min(material_index, len(materials) - 1)]
    material_object = pointer.deref() if pointer else None
    if material_object is None:
        return trimesh.visual.material.PBRMaterial(baseColorFactor=[160, 160, 160, 255])
    data = material_object.read()
    properties = data.m_SavedProperties
    colors = dict(properties.m_Colors)
    base = colors.get("_BaseColor", colors.get("_Color"))
    factor = [255, 255, 255, 255] if base is None else [
        int(max(0, min(255, round(float(channel) * 255))))
        for channel in (base.r, base.g, base.b, base.a)
    ]
    texture = None
    tex_envs = dict(properties.m_TexEnvs)
    for key in ("_BaseMap", "_MainTex"):
        env = tex_envs.get(key)
        if env is None or not env.m_Texture:
            continue
        texture_object = env.m_Texture.deref()
        if texture_object is None:
            continue
        texture_id = texture_object.path_id
        if texture_id not in texture_cache:
            try:
                texture_cache[texture_id] = texture_object.read().image
            except Exception:  # noqa: BLE001
                texture_cache[texture_id] = None
        texture = texture_cache[texture_id]
        if texture is not None:
            break
    return trimesh.visual.material.PBRMaterial(baseColorFactor=factor, baseColorTexture=texture)


def apply_material(mesh: trimesh.Trimesh, material) -> None:
    uv = getattr(mesh.visual, "uv", None)
    if uv is None:
        uv = np.zeros((len(mesh.vertices), 2), dtype=float)
    mesh.visual = trimesh.visual.TextureVisuals(uv=uv, material=material)


def main() -> int:
    if not APK.exists():
        print(f"missing {APK}", file=sys.stderr)
        return 1
    archive = zipfile.ZipFile(APK)
    env = UnityPy.Environment()
    scene_bundle = None
    for prefix in DEPENDENCIES:
        name = bundle_entry(archive, prefix)
        loaded = env.load_file(archive.read(name))
        if prefix == "world_town_scenes_all_":
            scene_bundle = name
    if scene_bundle is None:
        raise RuntimeError("Town scene bundle missing")

    scene_file = next(
        f for bundle in env.files.values() for f in bundle.files.values()
        if getattr(f, "name", "") == "CAB-09b08b485f1a010d625fc84d4f9ec161"
    )
    objects = scene_file.objects
    transform_cache: dict[int, np.ndarray] = {}
    game_objects = [obj for obj in objects.values() if obj.type.name == "GameObject"]

    spawn_points = {}
    light_sources = []
    player_spawn = None
    for obj in game_objects:
        data = obj.read()
        components = [pair.component.deref() for pair in data.m_Component]
        transform = next((component for component in components
                          if component is not None and component.type.name == "Transform"), None)
        if transform is None:
            continue
        world = transform_matrix(transform, transform_cache)
        position = world[:3, 3]
        if data.m_Name == PLAYER_SPAWN_NAME:
            player_spawn = position.copy()
        if data.m_Name.startswith("SpawnZone_"):
            spawn_points[data.m_Name.strip()] = {"x": float(position[0]), "z": float(position[2])}
        light_component = next((component for component in components
                                if component is not None and component.type.name == "Light"), None)
        if light_component is not None:
            light = light_component.read()
            if light.m_Enabled:
                color = light.m_Color
                light_sources.append({
                    "name": data.m_Name or f"TownLight_{obj.path_id}",
                    "type": int(light.m_Type),
                    "x": float(position[0]), "y": float(position[1]), "z": float(position[2]),
                    "direction": [float(-world[0, 2]), float(-world[1, 2]), float(-world[2, 2])],
                    "color": [float(color.r), float(color.g), float(color.b)],
                    "intensity": float(light.m_Intensity),
                    "range": float(light.m_Range),
                    "spotAngle": float(light.m_SpotAngle),
                })
    if player_spawn is None:
        raise RuntimeError("Town SpawnZone_Player not found")

    # Recenter the Unity town around its exact Player spawn. The export is scaled to fit the
    # web combat arena bounds; all NPC markers use the same transform below.
    town_transform = np.eye(4)
    town_transform[0, 0] = TOWN_SCALE
    town_transform[1, 1] = TOWN_SCALE
    town_transform[2, 2] = TOWN_SCALE
    town_transform[0, 3] = -float(player_spawn[0]) * TOWN_SCALE
    town_transform[2, 3] = -float(player_spawn[2]) * TOWN_SCALE

    output_scene = trimesh.Scene()
    texture_cache: dict[int, object] = {}
    mesh_cache: dict[int, object] = {}
    rendered = 0
    skipped = 0
    for obj in game_objects:
        data = obj.read()
        if not data.m_IsActive or data.m_Name in {"Fog", "Fog2"}:
            continue
        components = [pair.component.deref() for pair in data.m_Component]
        transform = next((component for component in components
                          if component is not None and component.type.name == "Transform"), None)
        mesh_filter = next((component for component in components
                            if component is not None and component.type.name == "MeshFilter"), None)
        renderer = next((component for component in components
                         if component is not None and component.type.name == "MeshRenderer"), None)
        if transform is None or mesh_filter is None or renderer is None:
            continue
        renderer_data = renderer.read()
        if not renderer_data.m_Enabled:
            continue
        mesh_pointer = mesh_filter.read().m_Mesh
        source_mesh = mesh_pointer.deref() if mesh_pointer else None
        if source_mesh is None:
            skipped += 1
            continue
        mesh_data = source_mesh.read()
        batch = renderer_data.m_StaticBatchInfo
        if batch.subMeshCount > 0:
            submeshes = range(batch.firstSubMesh, batch.firstSubMesh + batch.subMeshCount)
        else:
            submeshes = range(max(1, len(mesh_data.m_SubMeshes)))
        world_matrix = town_transform @ transform_matrix(transform, transform_cache)
        for local_index, submesh in enumerate(submeshes):
            geometry = geometry_for_submesh(mesh_data, submesh)
            if geometry is None:
                skipped += 1
                continue
            apply_material(geometry, material_for_renderer(renderer, local_index, texture_cache))
            geometry.apply_transform(world_matrix)
            name = re.sub(r"[^A-Za-z0-9_.-]", "_", data.m_Name or "TownObject")
            output_scene.add_geometry(geometry, geom_name=f"{name}_{obj.path_id}_{submesh}",
                                      node_name=f"{name}_{obj.path_id}_{submesh}")
            rendered += 1

    OUT.mkdir(parents=True, exist_ok=True)
    glb_path = OUT / "town_scene.glb"
    glb_path.write_bytes(output_scene.export(file_type="glb"))
    manifest = {
        "baseline": "android-1.9.3 (versionCode 507033)",
        "sourceBundle": scene_bundle,
        "sourceBundleSha256": hashlib.sha256(archive.read(scene_bundle)).hexdigest(),
        "playerSpawn": {"x": 0.0, "z": 0.0},
        "scale": TOWN_SCALE,
        "lights": [
            light | {
                "x": (light["x"] - float(player_spawn[0])) * TOWN_SCALE,
                "y": light["y"] * TOWN_SCALE,
                "z": (light["z"] - float(player_spawn[2])) * TOWN_SCALE,
                "range": light["range"] * TOWN_SCALE,
            }
            for light in light_sources
        ],
        "meshes": rendered,
        "skipped": skipped,
        "spawnZones": [
            {"name": name.removeprefix("SpawnZone_"),
             "x": (point["x"] - float(player_spawn[0])) * TOWN_SCALE,
             "z": (point["z"] - float(player_spawn[2])) * TOWN_SCALE}
            for name, point in sorted(spawn_points.items())
        ],
    }
    (OUT / "town_scene.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"wrote {glb_path} ({glb_path.stat().st_size / 1024 / 1024:.1f} MiB), {rendered} mesh instances, skipped {skipped}")
    print(f"wrote {OUT / 'town_scene.json'} with {len(spawn_points)} spawn zones")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
