#!/usr/bin/env python3
"""Inject the email/password login stub into the original 1.9.3 ARM64 lib.

Applies the standard online patches (patch_android_online.patch) and then:

  * writes device/stub/stub.bin (.text + .rodata) at 0x344EC24,
  * patches UIWindowManager.ShowSingleInputDialogOkCancel (0x0279075C) so its
    first instruction becomes `b email_capture_trampoline` (0x344F0A0) — this
    seeds the real System.Action<string> and the UIWindowManager instance the
    stub needs,
  * patches WindowSelectGameMode.OnSignInClicked (0x026340E0) so the "Sign in"
    button runs email_login_entry (0x344EC24).

Run patch_xapk.py afterwards (or call this from it) to rebuild the APK.
"""
import argparse
import hashlib
import struct
from pathlib import Path

from patch_android_online import patch as patch_online

STUB_VA = 0x344EC24
TRAMPOLINE_VA = 0x344F0A0
ENTRY_VA = 0x344EC24
SHOWDLG = 0x0279075C
ONSIGNIN = 0x026340E0
REFRESH = 0x02633BD0
WMGR_INIT = 0x0278CD8C
WMGR_TRAMP = 0x344F0E0


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


def _bl(frm, to):
    return struct.pack("<I", 0x94000000 | (((to - frm) // 4) & 0x3FFFFFF))


def build(src: Path, out: Path, stub_bin: Path):
    data = patch_online(src.read_bytes())              # standard online patches
    result = bytearray(data)
    segs = _segments(bytes(data))

    blob = stub_bin.read_bytes()
    o = _off_for(segs, STUB_VA)
    assert len(blob) <= 0x345031C - STUB_VA, "stub does not fit the code cave"
    result[o:o + len(blob)] = blob

    # ShowSingleInputDialogOkCancel -> capture trampoline
    o = _off_for(segs, SHOWDLG)
    original = bytes(result[o:o + 4])
    assert original == bytes.fromhex("ffc301d1"), f"unexpected prologue {original.hex()}"
    result[o:o + 4] = _bl(SHOWDLG, TRAMPOLINE_VA)

    # OnSignInClicked -> email login entry
    o = _off_for(segs, ONSIGNIN)
    result[o:o + 4] = _bl(ONSIGNIN, ENTRY_VA)

    # Seed the UIWindowManager instance from UIWindowManager.Init(x0 = instance).
    o = _off_for(segs, WMGR_INIT)
    assert bytes(result[o:o+4]) == bytes.fromhex("fe0f1bf8"), "unexpected UIWindowManager.Init prologue"
    result[o:o + 4] = _bl(WMGR_INIT, WMGR_TRAMP)

    # RefreshSignInButton normally destroys the "Sign in" button; keep it.
    o = _off_for(segs, REFRESH)
    result[o:o + 4] = bytes.fromhex("c0035fd6")   # ret

    out.write_bytes(bytes(result))
    print(f"injected {len(blob)} bytes at {STUB_VA:#x}")
    print(f"showdlg   {SHOWDLG:#x} -> b {TRAMPOLINE_VA:#x}")
    print(f"signin    {ONSIGNIN:#x} -> b {ENTRY_VA:#x}")
    print(f"wrote {out}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("source", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--stub", type=Path,
                    default=Path(__file__).resolve().parent.parent / "device/stub/stub.bin")
    a = ap.parse_args()
    build(a.source, a.output, a.stub)