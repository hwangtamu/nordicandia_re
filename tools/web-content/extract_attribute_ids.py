#!/usr/bin/env python3
"""Recover the GameAttribute numeric id -> name table from the desktop cctor listing.

`steam_analysis` contains `allattrs.asm` (the disassembly of GameAttributes..cctor).
Each attribute is built with:

    mov rax, qword ptr [rip + ...]   <Name>
    mov dx, <id>
    call GameAttributeD__ctor_1(this, id, defaultValue, script, name, ...)

This maps the mastery `AttributeId` values onto real attribute names so mastery
modifiers can be interpreted.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/extract_attribute_ids.py
Output:
  tools/web-content/generated/attribute_ids.json
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
ASM = ROOT / "tmp/allattrs.asm"
OUT = ROOT / "tools/web-content/generated/attribute_ids.json"

NAME_LINE = re.compile(r"mov\s+rax, qword ptr \[rip \+ 0x[0-9a-f]+\]\s+(\S.*)$")
ID_LINE = re.compile(r"mov\s+dx, (0x[0-9a-fA-F]+|\d+)")
CTOR = "GameAttributeD__ctor_1"


def main() -> int:
    if not ASM.exists():
        print(f"missing {ASM}", file=sys.stderr)
        return 1
    lines = ASM.read_text(errors="ignore").splitlines()
    table: dict[int, str] = {}
    last_name: str | None = None
    last_id: int | None = None
    for line in lines:
        m = NAME_LINE.search(line)
        if m:
            comment = m.group(1).strip()
            if not comment.startswith("_ZN") and not comment.startswith("void "):
                last_name = comment
        m = ID_LINE.search(line)
        if m:
            last_id = int(m.group(1), 0)
        if CTOR in line:
            if last_id is not None and last_name:
                table.setdefault(last_id, last_name)
            last_id = None
            last_name = None
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({str(k): v for k, v in sorted(table.items())}, indent=2))
    print(f"wrote {OUT}: {len(table)} attribute ids")
    for pid in (58, 79, 80, 109, 143, 166, 193, 205, 232, 264, 267, 717, 850):
        print(f"  {pid} = {table.get(pid)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
