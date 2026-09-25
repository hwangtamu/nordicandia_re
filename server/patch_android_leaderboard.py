#!/usr/bin/env python3
"""Wire the Android client's leaderboard window to the private server.

The shipped 1.9.3 Android build renders a locally synthesised board
(Nordicandia.Client.Offline.OfflineFakeLeaderboard) and never calls the
MagicOnion leaderboard API, so characters that only exist server side (e.g.
Steam characters) can never show up. This patch replaces the three
OfflineFakeLeaderboard.Build* entry points with a stub that fetches the real
cross-platform standings from the server's plain-HTTP JSON feed and returns
them as a FakeLeaderboardRow[] (FakeLeaderboardRow is a struct; see the notes
in device/stub/leaderboard_stub.c).

Applies the standard online patches first, then:

  * compiles device/stub/leaderboard_stub.c (clang + ld.lld + llvm-objcopy) -
    the stub is always rebuilt from source so a stale prebuilt blob can no
    longer be injected silently; ``--stub/--elf`` still accept a prebuilt pair,
  * writes the blob at 0x3451000 (inside the unused BestHTTP Examples demo code
    region, 0x344EBFC..0x345C9AC; the realtime stub starts at 0x3456000),
  * patches 0x02E3FABC / 0x02E40264 / 0x02E404F0 with a plain B to the stub
    entry points (lb_overall / lb_class / lb_helheim, read from the linked ELF),
  * hooks row creation and Button.OnPointerClick's left-click branch so tapping a
    row's "Button (Inspect)" maps the player's name to their server Guid and
    opens WindowInspectPlayer,
  * turns WindowInspectPlayer(.Skills).Awake into `ret` (the offline build made
    them destroy their own GameObject, so the inspect window never survived),
  * grows the last RW PT_LOAD p_memsz so the stub's private state page
    (0x5DE6000..0x5DE7000, above the retail .bss ending at 0x5DDE840) is mapped.
"""
import argparse
import hashlib
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

from patch_android_online import patch as patch_online
from patch_android_email_login import _read_symbols

ROOT = Path(__file__).resolve().parent.parent
STUB_DIR = ROOT / "device/stub"
STUB_VA, STUB_END = 0x3451000, 0x3456000          # realtime stub owns 0x3456000+
STATE_VA, STATE_END = 0x5DE6000, 0x5DE7000
RETAIL_BSS_END = 0x5DDE840

BUILD_OVERALL = 0x02E3FABC
BUILD_CLASS = 0x02E40264
BUILD_HELHEIM = 0x02E404F0
CREATE_ROW_APPLY_RANK = 0x025F72B4
BUTTON_LEFT_CLICK = 0x04F76140   # Button.OnPointerClick: `b Button.Press` (left button)
# The offline build turned these Awake methods into UnityGame.DestroyObject(gameObject),
# so an instantiated "Window (Inspect Player)" deleted itself on its first frame.
INSPECT_AWAKE = 0x025F3310          # WindowInspectPlayer.Awake
INSPECT_SKILLS_AWAKE = 0x025F3AAC   # WindowInspectPlayerSkills.Awake
RET = bytes.fromhex("c0035fd6")

# il2cpp exports the stub links against (libil2cpp 1.9.3 arm64-v8a).
IL2CPP = {
    "il2cpp_string_new": 0x02318578,
    "il2cpp_alloc": 0x02317C14,
    "il2cpp_array_new": 0x02317C28,
    "il2cpp_class_from_name": 0x02317C74,
    "il2cpp_domain_get": 0x02318138,
    "il2cpp_domain_get_assemblies": 0x02318144,
    "il2cpp_assembly_get_image": 0x02317C3C,
    "il2cpp_runtime_class_init": 0x02318560,
    "il2cpp_class_is_valuetype": 0x02317CB8,
    "il2cpp_class_value_size": 0x02317CD4,
    "il2cpp_gc_wbarrier_set_field": 0x02318414,
    "il2cpp_class_get_method_from_name": 0x02317C9C,
    "il2cpp_runtime_invoke": 0x0231855C,
    "il2cpp_object_get_class": 0x02318508,
    "il2cpp_class_get_type": 0x02317D1C,
    "il2cpp_type_get_object": 0x023185E8,
    "retail_apply_rank": 0x025F771C,
    "retail_create_row_resume": 0x025F72B8,
    "retail_button_press": 0x04F76098,
    "retail_get_window": 0x0278C698,
    "retail_bring_front": 0x0278BBF0,
    "retail_instantiate_9": 0x0379B140,
    "retail_instantiate_method_slot": 0x055893A8,
    "retail_inspect_player": 0x025F33E4,
    "retail_forget": 0x0486610C,
    "retail_component_get_transform": 0x04E40208,
}


def _tool(name):
    path = shutil.which(name)
    if path:
        return path
    for directory in ("/opt/homebrew/opt/llvm/bin", "/usr/local/opt/llvm/bin"):
        candidate = Path(directory) / name
        if candidate.exists():
            return str(candidate)
    raise RuntimeError(f"{name} is required to build the leaderboard stub (brew install llvm lld)")


def build_stub(directory: Path):
    """Compile + link the stub at its real VA; returns (blob, symbols)."""
    obj, elf, binf = directory / "lbstub.o", directory / "lbstub.elf", directory / "leaderboard_stub.bin"
    subprocess.run([_tool("clang"), "-target", "aarch64-linux-gnu", "-O2", "-ffreestanding",
                    "-fno-stack-protector", "-fno-pic", "-mno-outline-atomics",
                    "-fno-jump-tables", "-c", str(STUB_DIR / "leaderboard_stub.c"), "-o", str(obj)],
                   check=True)
    subprocess.run([_tool("ld.lld"), "-T", str(STUB_DIR / "lbstub.ld"),
                    *[f"--defsym={k}={v:#x}" for k, v in IL2CPP.items()],
                    "-o", str(elf), str(obj)], check=True)
    subprocess.run([_tool("llvm-objcopy"), "-O", "binary", "--only-section=.text",
                    "--only-section=.rodata", str(elf), str(binf)], check=True)
    return binf.read_bytes(), _read_symbols(elf), elf


def _check_stub_elf(elf: Path, blob: bytes):
    """Refuse blobs that cannot work once relocated by ASLR or that lose data."""
    data = elf.read_bytes()
    shoff = struct.unpack_from("<Q", data, 0x28)[0]
    shentsize, shnum, shstrndx = struct.unpack_from("<HHH", data, 0x3A)
    secs = [struct.unpack_from("<IIQQQQIIQQ", data, shoff + i * shentsize) for i in range(shnum)]
    strtab = secs[shstrndx][4]
    for s in secs:
        name = data[strtab + s[0]:data.index(b"\0", strtab + s[0])].decode()
        typ, flags, addr, size = s[1], s[2], s[3], s[5]
        if name.startswith(".data") and size:
            raise ValueError(f"stub has initialised writable data ({name}); objcopy would drop it")
        if name == ".bss" and size and not (STATE_VA <= addr and addr + size <= STATE_END):
            raise ValueError(f".bss {addr:#x}+{size:#x} outside the private state page")
    # Absolute pointers into the stub itself (pointer tables, lookup tables) are
    # link-time addresses and are wrong under ASLR: nothing relocates this blob.
    for off in range(0, len(blob) - 7, 8):
        word = struct.unpack_from("<Q", blob, off)[0]
        if STUB_VA <= word < STUB_END or STATE_VA <= word < STATE_END:
            raise ValueError(f"absolute pointer {word:#x} at blob+{off:#x}: stub must be position independent")


def _extend_rw_memsz(result: bytearray, state_end: int):
    phoff = struct.unpack_from("<Q", result, 32)[0]
    entsize, count = struct.unpack_from("<HH", result, 54)
    target = None
    for i in range(count):
        base = phoff + i * entsize
        typ, flags, off, va, _, filesz, memsz, _ = struct.unpack_from("<IIQQQQQQ", result, base)
        if typ == 1 and (flags & 2) and va < state_end:
            if target is None or va > target[1]:
                target = (base, va, memsz)
    if target is None:
        raise ValueError("no writable PT_LOAD to extend")
    base, va, memsz = target
    if va + memsz < RETAIL_BSS_END:
        raise ValueError("unexpected RW PT_LOAD layout")
    if va + memsz < state_end:
        struct.pack_into("<Q", result, base + 40, state_end - va)
        print(f"grew RW PT_LOAD p_memsz to {state_end - va:#x} (state at {STATE_VA:#x})")


def _segments(data):
    phoff = struct.unpack_from("<Q", data, 32)[0]
    entsize, count = struct.unpack_from("<HH", data, 54)
    segs = []
    for i in range(count):
        typ, flags, off, va, _, size, _, _ = struct.unpack_from("<IIQQQQQQ", data, phoff + i * entsize)
        if typ == 1:
            segs.append((va, off, size))
    return segs


def _off_for(segs, va):
    for start, off, size in segs:
        if start <= va < start + size:
            return off + va - start
    raise ValueError(f"{va:#x} not in any segment")


def _b(frm, to):
    """Plain branch (B): must not touch x30, the patched function keeps its caller's LR."""
    return struct.pack("<I", 0x14000000 | (((to - frm) // 4) & 0x3FFFFFF))


def apply(data: bytes, blob: bytes, syms: dict) -> bytes:
    result = bytearray(data)
    segs = _segments(bytes(data))

    if len(blob) > STUB_END - STUB_VA:
        raise ValueError("stub does not fit its code cave (would overlap the realtime stub)")
    for name in ("lb_overall", "lb_class", "lb_helheim", "lb_row_hook", "lb_click_hook"):
        if not STUB_VA <= syms[name] < STUB_VA + len(blob):
            raise ValueError(f"{name} lies outside the injected blob")
    o = _off_for(segs, STUB_VA)
    result[o:o + len(blob)] = blob
    print(f"injected {len(blob)} bytes at {STUB_VA:#x}")

    for name, site, dest, prologue in (
        ("BuildOverallBoard", BUILD_OVERALL, syms["lb_overall"], "fe5fbda9"),
        ("BuildClassBoard", BUILD_CLASS, syms["lb_class"], "fe0f1bf8"),
        ("BuildHelheimBoard", BUILD_HELHEIM, syms["lb_helheim"], "fe0f1cf8"),
    ):
        o = _off_for(segs, site)
        if bytes(result[o:o + 4]) != bytes.fromhex(prologue):
            raise ValueError(f"unexpected {name} prologue {bytes(result[o:o + 4]).hex()}")
        result[o:o + 4] = _b(site, dest)
        print(f"{name:20s} {site:#010x} -> b {dest:#x}")

    for name, site, dest, expected in (
        ("CreateRow register", CREATE_ROW_APPLY_RANK, syms["lb_row_hook"], bytes.fromhex("1a010094")),
        ("Button left-click", BUTTON_LEFT_CLICK, syms["lb_click_hook"], bytes.fromhex("d6ffff17")),
    ):
        o = _off_for(segs, site)
        if bytes(result[o:o + 4]) != expected:
            raise ValueError(f"unexpected {name} instruction {bytes(result[o:o + 4]).hex()}")
        result[o:o + 4] = _b(site, dest)
        print(f"{name:20s} {site:#010x} -> b {dest:#x}")

    for name, site in (("WindowInspectPlayer.Awake", INSPECT_AWAKE),
                       ("WindowInspectPlayerSkills.Awake", INSPECT_SKILLS_AWAKE)):
        o = _off_for(segs, site)
        if bytes(result[o:o + 4]) != bytes.fromhex("fe57bea9"):
            raise ValueError(f"unexpected {name} prologue {bytes(result[o:o + 4]).hex()}")
        result[o:o + 4] = RET
        print(f"{name:20s} {site:#010x} -> ret (was Destroy(gameObject))")

    _extend_rw_memsz(result, STATE_END)
    return bytes(result)


def build(src: Path, out: Path, stub_bin: Path = None, stub_elf: Path = None):
    data = patch_online(src.read_bytes())
    with tempfile.TemporaryDirectory(prefix="nord-lb-") as work:
        if stub_bin:
            elf = stub_elf or stub_bin.with_name("lbstub.elf")
            blob, syms = stub_bin.read_bytes(), _read_symbols(elf)
        else:
            blob, syms, elf = build_stub(Path(work))
            for name in ("lbstub.elf", "leaderboard_stub.bin"):
                shutil.copy2(Path(work) / name, STUB_DIR / name)
        _check_stub_elf(elf, blob)
        result = apply(data, blob, syms)
    out.write_bytes(result)
    print(f"wrote {out} sha256={hashlib.sha256(result).hexdigest()}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--stub", type=Path, help="prebuilt leaderboard_stub.bin (default: build from source)")
    ap.add_argument("--elf", type=Path, help="ELF matching --stub (default: lbstub.elf next to it)")
    args = ap.parse_args()
    build(args.src, args.out, args.stub, args.elf)


if __name__ == "__main__":
    main()
