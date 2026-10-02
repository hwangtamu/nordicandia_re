#!/usr/bin/env python3
"""Map each scripted game attribute to its client synthesis formula.

`tmp/allattrs.asm` is the desktop `GameAttributes` static construction: every scripted
attribute is built by a call to `GameAttributeD__ctor_1(this, id, defaultValue, script,
name, ...)`. The formula (`script`) is loaded into `r9` and the attribute's own name into
`[rsp+0x20]` just before the call; the `id` (int16 enum value) is loaded into `dx`.

This pairs the enum id and formula so the web side knows which expression defines which
attribute (e.g. 0x10d `Armor_SubTotal` => `((Armor + Flat_Armor_From_Constitution) * ...)`).
Attribute names are resolved from `attribute_ids.json`.

Usage:
  python3 tools/web-content/extract_attribute_formulas.py
Output:
  tools/web-content/generated/attribute_formulas.json
"""
from __future__ import annotations

import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]
ASM = ROOT / "tmp/allattrs.asm"
IDS = ROOT / "tools/web-content/generated/attribute_ids.json"
OUT = ROOT / "tools/web-content/generated/attribute_formulas.json"

CTOR = re.compile(r"call\s+0x[0-9a-f]+\s+.*GameAttribute(?:D|DA|I|IA)__ctor")
DX = re.compile(r"mov\s+dx, (0x[0-9a-f]+)")
SCRIPT_REG = re.compile(r"mov\s+(r[89]), qword ptr \[rip \+ 0x[0-9a-f]+\]\s+(.*)$")


def main() -> int:
    if not ASM.exists():
        print(f"missing {ASM}", file=sys.stderr)
        return 1
    name_by_id = {int(k): v for k, v in json.loads(IDS.read_text()).items()}

    rows: dict[int, dict[str, str]] = {}
    last_id: int | None = None
    last_script: str | None = None
    for line in ASM.read_text(errors="ignore").splitlines():
        if CTOR.search(line):
            if last_id is not None and last_script and re.search(r"[()]", last_script):
                rows[last_id] = {"name": name_by_id.get(last_id, "?"), "script": last_script}
            last_id = last_script = None
            continue
        m = DX.search(line)
        if m:
            last_id = int(m.group(1), 16)
        m = SCRIPT_REG.search(line)
        # Prefer an expression-looking string; a plain identifier is the name, not the script.
        if m and re.search(r"[()]", m.group(2)):
            last_script = m.group(2).strip()

    OUT.write_text(json.dumps({str(k): v for k, v in rows.items()}, indent=2))
    print(f"wrote {OUT}: {len(rows)} attribute formulas")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
