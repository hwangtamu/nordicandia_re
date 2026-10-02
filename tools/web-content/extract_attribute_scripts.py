#!/usr/bin/env python3
"""Extract the client's scripted attribute formulas from the desktop cctor listing.

`GameAttributeD` derived attributes carry a "script" expression that defines how a
*Total* attribute is synthesised from its Base/Bonus parts (e.g.
`(Base_Power_Radius) * (1 + Power_Radius_Bonus_Percent)`). `tmp/allattrs.asm` contains
those expression strings as string-literal loads. This collects them so the web
server can reference the exact client structure.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/extract_attribute_scripts.py
Output:
  tools/web-content/generated/attribute_scripts.json
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
ASM = ROOT / "tmp/allattrs.asm"
OUT = ROOT / "tools/web-content/generated/attribute_scripts.json"

LEA = re.compile(r"lea\s+rcx, \[rip \+ 0x[0-9a-f]+\]\s+(\S.*)$")


def main() -> int:
    if not ASM.exists():
        print(f"missing {ASM}", file=sys.stderr)
        return 1
    scripts = []
    for line in ASM.read_text(errors="ignore").splitlines():
        m = LEA.search(line)
        if not m:
            continue
        text = m.group(1).strip()
        if text.startswith("_ZN") or " " not in text and "(" not in text:
            continue
        if not re.search(r"[()*/+-]", text):
            continue
        if "(" not in text:
            continue
        scripts.append(text)
    scripts = sorted(set(scripts))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(scripts, indent=2))
    print(f"wrote {OUT}: {len(scripts)} attribute formulas")
    for s in scripts:
        if any(k in s for k in ("Weapon_Physical_Damage_Percent", "Armor_", "Crit_Chance", "Crit_Damage")):
            print("  ", s[:120])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
