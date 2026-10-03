#!/usr/bin/env python3
"""Export the B02 full affix denominator (all 1837 Affixes.json entries).

GenerationType semantics (client RE, AffixCatalog.cs): Prefix=0, Suffix=1,
Implicit=2, Set=3, Unique=4.

Per docs/web/B02_DEPRECATION_RULES.md each entry is annotated with:
  identity (guid/integerId/name), generationType, domain, group membership,
  granted attribute ids/names, status, and whether it is in the curated
  server GameData/affix_catalog.json (18 entries).

Output: tools/web-content/generated/affix_catalog_full.json
"""
from __future__ import annotations

import datetime
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
GENERATED = ROOT / "tools" / "web-content" / "generated"
CURATED = ROOT / "server" / "Nordicandia.Server" / "GameData" / "affix_catalog.json"
OUT = GENERATED / "affix_catalog_full.json"

BASELINE = "android-1.9.3 (versionCode 507033)"
GEN_LABELS = {0: "Prefix", 1: "Suffix", 2: "Implicit", 3: "Set", 4: "Unique"}


def main() -> None:
    affixes = json.loads((GAMEDATA / "Affixes.json").read_text())
    attr_ids = json.loads((GENERATED / "attribute_ids.json").read_text())
    id_to_name = {v: k for k, v in attr_ids.items()} if isinstance(attr_ids, dict) else {}
    curated = set(json.loads(CURATED.read_text()).keys())

    items = []
    for x in affixes:
        specs = x.get("AttributeSpecifierDefinitionList") or []
        attr_ids_granted = [s.get("AttributeId") for s in specs if s.get("AttributeId") is not None]
        gen = x.get("GenerationType")
        rarities = set()
        for s in specs:
            for vr in s.get("ValueRangeByRarityList") or []:
                if vr.get("Rarity") is not None:
                    rarities.add(vr["Rarity"])
        items.append({
            "guid": x["Id"],
            "integerId": x.get("IntegerId"),
            "name": x.get("Name"),
            "generationType": gen,
            "generationLabel": GEN_LABELS.get(gen, "未知" if gen is None else f"未定义({gen})"),
            "domain": x.get("Domain"),
            "hasGroup": x.get("GroupId") is not None,
            "attributeIds": attr_ids_granted,
            "attributeNames": [id_to_name.get(i, f"id:{i}") for i in attr_ids_granted],
            "rarityTiers": sorted(rarities),
            "status": "待核实" if gen is None else "正式",
            "inCurrentCatalog": x.get("Name") in curated,
        })

    items.sort(key=lambda i: ((i["generationType"] if i["generationType"] is not None else 99),
                              i["name"] or ""))
    by_gen: dict = {}
    for i in items:
        by_gen[i["generationLabel"]] = by_gen.get(i["generationLabel"], 0) + 1
    out = {
        "baseline": BASELINE,
        "generatedAt": datetime.date.today().isoformat(),
        "generationTypeSemantics": "Prefix=0, Suffix=1, Implicit=2, Set=3, Unique=4（客户端反汇编，见 AffixCatalog.cs）",
        "counts": {
            "total": len(items),
            "byGeneration": by_gen,
            "inCurrentCatalog": sum(1 for i in items if i["inCurrentCatalog"]),
            "pendingVerification": sum(1 for i in items if i["status"] == "待核实"),
        },
        "items": items,
    }
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT} ({len(items)} affixes)")


if __name__ == "__main__":
    main()
