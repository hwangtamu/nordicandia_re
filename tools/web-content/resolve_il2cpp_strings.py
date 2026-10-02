#!/usr/bin/env python3
"""Resolve IL2CPP string literals referenced by Cpp2IL ISIL dumps.

On v27+ Android builds the generated code loads a string literal from a GOT slot
that is zero in the file and populated at runtime. The ELF `.rela.dyn` relocation
for that GOT slot holds (as its addend) the address of the metadata-usage token,
and `android-metadata.json`'s `addressMap.stringLiterals` maps that token address
to the literal text. Chain the three and every `Move vX, [GOT]` becomes readable.

Inputs:
  tmp/apk-libil2cpp.so                         (for .rela.dyn)
  tmp/android-metadata.json                    (addressMap.stringLiterals)
  a Cpp2IL `isil` output directory (IsilDump)
Outputs:
  tmp/got_to_string.json                       (GOT address -> literal)
  tools/web-content/generated/method_strings.json
                                               (method signature -> sorted literals)

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/resolve_il2cpp_strings.py \
      --isil tmp/cpp2il-isil2/IsilDump [--only-substr Game/Items]
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import struct

ROOT = pathlib.Path(__file__).resolve().parents[2]
SO = ROOT / "tmp" / "apk-libil2cpp.so"
META = ROOT / "tmp" / "android-metadata.json"
GOT_MAP = ROOT / "tmp" / "got_to_string.json"
OUT_MAP = ROOT / "tools" / "web-content" / "generated" / "method_strings.json"

MOVE = re.compile(r"Move (v\d+)[^,]*, \[([0-9A-Fa-f]{6,})\]")
METHOD = re.compile(r"^Method: (.*)$")


def build_got_map() -> dict[int, str]:
    data = SO.read_bytes()
    e_shoff = struct.unpack_from("<Q", data, 0x28)[0]
    e_shentsize = struct.unpack_from("<H", data, 0x3A)[0]
    e_shnum = struct.unpack_from("<H", data, 0x3C)[0]
    e_shstrndx = struct.unpack_from("<H", data, 0x3E)[0]

    def section(i: int):
        return struct.unpack_from("<IIQQQQIIQQ", data, e_shoff + i * e_shentsize)

    shstr = section(e_shstrndx)
    str_base = shstr[4]

    def name_at(off: int) -> str:
        end = data.index(b"\0", str_base + off)
        return data[str_base + off:end].decode(errors="ignore")

    literals = {
        int(x["virtualAddress"], 16): x["string"]
        for x in json.loads(META.read_text())["addressMap"]["stringLiterals"]
    }

    got: dict[int, str] = {}
    for i in range(e_shnum):
        name, _, _, _, off, size, _, _, _, entsize = section(i)
        if "rela" not in name_at(name).lower():
            continue
        for j in range(size // entsize):
            r_offset, r_info, r_addend = struct.unpack_from("<QQq", data, off + j * entsize)
            if (r_info & 0xFFFFFFFF) == 1027 and r_addend in literals:  # R_AARCH64_RELATIVE
                got[r_offset] = literals[r_addend]
    return got


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--isil", required=True)
    ap.add_argument("--only-substr", default="")
    ap.add_argument("--annotate-out", default="", help="optional dir to write copies with resolved strings")
    args = ap.parse_args()

    got = build_got_map()
    GOT_MAP.write_text(json.dumps({str(k): v for k, v in got.items()}))
    print(f"GOT entries resolving to string literals: {len(got)}")

    merged: dict[str, set[str]] = {}
    isil = pathlib.Path(args.isil)
    out_root = pathlib.Path(args.annotate_out) if args.annotate_out else None
    for src in isil.rglob("*.txt"):
        if args.only_substr and args.only_substr not in str(src):
            continue
        method = None
        out_lines: list[str] = []
        for line in src.read_text(errors="ignore").splitlines():
            m = METHOD.match(line)
            if m:
                method = m.group(1)
                merged.setdefault(method, set())
                out_lines.append(line)
                continue
            m = MOVE.search(line)
            if m and method:
                addr = int(m.group(2), 16)
                if addr in got:
                    merged[method].add(got[addr])
                    line = f'{line}    ; "{got[addr]}"'
            out_lines.append(line)
        if out_root is not None:
            dst = out_root / src.relative_to(isil)
            dst.parent.mkdir(parents=True, exist_ok=True)
            dst.write_text("\n".join(out_lines) + "\n")
    result = {k: sorted(v) for k, v in merged.items() if v}

    OUT_MAP.write_text(json.dumps(result, indent=2, sort_keys=True))
    print(f"files with resolved literals: {len(result)} -> {OUT_MAP}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
