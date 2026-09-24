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

# The stub moves whenever it is edited, which shifts both the entry point and the
# capture trampolines. Hard-coding them silently produced a branch into the middle
# of the blob (crash at the patched call site), so they are read from the linked
# ELF instead.
_DEFAULT_SYMS = {
    "email_capture_trampoline": 0x344EE9C,
    "email_capture_wm_trampoline": 0x344EEDC,
    "email_login_entry": 0x344EC24,
}


def _read_symbols(elf: Path) -> dict:
    """Minimal ELF64 symbol-table reader (no external tools required)."""
    data = elf.read_bytes()
    if data[:4] != b"\x7fELF":
        raise ValueError(f"{elf} is not an ELF file")
    shoff = struct.unpack_from("<Q", data, 0x28)[0]
    shentsize, shnum, shstrndx = struct.unpack_from("<HHH", data, 0x3A)
    sections = []
    for i in range(shnum):
        o = shoff + i * shentsize
        sections.append(struct.unpack_from("<IIQQQQIIQQ", data, o))

    def sname(i):
        o = sections[shstrndx][4] + i
        return data[o:data.index(b"\x00", o)].decode()

    out = {}
    for i, sec in enumerate(sections):
        name, typ, flags, addr, off, size, link, info, align, entsize = sec
        if typ not in (2, 11) or entsize == 0:          # SYMTAB / DYNSYM
            continue
        stroff = sections[link][4]
        for j in range(size // entsize):
            o = off + j * entsize
            st_name, _, _, _, st_value, _ = struct.unpack_from("<IBBHQQ", data, o)
            if st_name == 0 or st_value == 0:
                continue
            nm = data[stroff + st_name:data.index(b"\x00", stroff + st_name)].decode()
            out.setdefault(nm, st_value)
    return out


def _stub_symbols(stub_bin: Path) -> dict:
    syms = dict(_DEFAULT_SYMS)
    elf = stub_bin.with_suffix(".elf")
    if elf.exists():
        try:
            found = _read_symbols(elf)
            # Merge EVERY symbol the linked stub exposes: the defaults are only a
            # fallback for when stub.elf is unavailable. Filtering by the default key
            # set silently dropped newly added trampolines and produced KeyError.
            syms.update(found)
        except Exception as exc:
            print(f"warning: falling back to default stub symbols ({exc})")
    return syms
SAVECHAR = 0x025D18D8
SHOWDLG = 0x0279075C
ONSIGNIN = 0x026340E0
REFRESH = 0x02633BD0
# LoginStateMachineNew.Login is the single funnel every sign-in passes through. Its
# arguments are rewritten to the credential-file email login so the account is online
# from the first frame (that is what makes PlayerAccount.IsOnline true and therefore
# opens the realtime /ws channel which persists experience).
LOGIN_FUNNEL = 0x026F2434
# WindowSelectGameMode.OnEnable: the login machine is created with this window, so the
# credential-file sign-in is triggered from here (in-window UI context).
GAMEMODE_ONENABLE = 0x02633568
GAMEMODE_START = 0x026337B4
# LoadGame.SignInNew drives the *startup* sign-in (device flow). Routing it through the
# stub makes the credential-file email login the default, so the account is online from
# the very first frame instead of only after tapping "Sign in".
LOADGAME_SIGNIN = 0x025B7918
WMGR_INIT = 0x0278D738
WMGR_TRAMP = 0x344EE44      # email_capture_wm_trampoline


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
    # plain branch (B, opcode 0x14...): must NOT set x30 or the patched
    # function would return to the patch site instead of its caller.
    return struct.pack("<I", 0x14000000 | (((to - frm) // 4) & 0x3FFFFFF))


def build(src: Path, out: Path, stub_bin: Path):
    data = patch_online(src.read_bytes())              # standard online patches
    result = bytearray(data)
    segs = _segments(bytes(data))

    syms = _stub_symbols(stub_bin)
    syms.setdefault("login_force_trampoline", 0x344EEA0)
    tramp = syms["email_capture_trampoline"]
    entry = syms["email_login_entry"]
    wm_tramp = syms["email_capture_wm_trampoline"]
    login_tramp = syms["login_force_trampoline"]
    blob = stub_bin.read_bytes()
    o = _off_for(segs, STUB_VA)
    assert len(blob) <= 0x345031C - STUB_VA, "stub does not fit the code cave"
    for label, va in (("trampoline", tramp), ("entry", entry), ("wm trampoline", wm_tramp),
                      ("login trampoline", login_tramp)):
        assert STUB_VA <= va < STUB_VA + len(blob), f"{label} {va:#x} lies outside the injected blob"
    result[o:o + len(blob)] = blob

    # ShowSingleInputDialogOkCancel -> capture trampoline
    o = _off_for(segs, SHOWDLG)
    original = bytes(result[o:o + 4])
    assert original == bytes.fromhex("ffc301d1"), f"unexpected prologue {original.hex()}"
    result[o:o + 4] = _b(SHOWDLG, tramp)

    # OnSignInClicked -> email login entry
    o = _off_for(segs, ONSIGNIN)
    result[o:o + 4] = _b(ONSIGNIN, entry)

    # Seed the UIWindowManager instance from UIWindowManager.Update (called every
    # frame with x0 = instance). A first attempt through Init crashed, so Update
    # is used instead.
    o = _off_for(segs, WMGR_INIT)
    assert bytes(result[o:o+4]) == bytes.fromhex("ffc302d1"), "unexpected UIWindowManager.Update prologue"
    result[o:o + 4] = _b(WMGR_INIT, wm_tramp)

    # RefreshSignInButton normally destroys the "Sign in" button; keep it.
    # NOTE: hooking LoadGame.SignInNew to switch the *startup* sign-in to the email
    # flow runs before NetClient exists and the client then reports "Client not
    # initialized", so the online login is triggered from the UI instead.

    # NOTE: rewriting LoginStateMachineNew.Login's arguments runs before NetClient
    # exists and the client then reports "Client not initialized"; the credential-file
    # sign-in is instead triggered automatically from the per-frame UIWindowManager
    # update trampoline once the startup device login has initialised the client.

    o = _off_for(segs, GAMEMODE_ONENABLE)
    result[o:o + 4] = _b(GAMEMODE_ONENABLE, syms["game_mode_onenable_trampoline"])
    print(f"onEnable  {GAMEMODE_ONENABLE:#010x} -> b {syms['game_mode_onenable_trampoline']:#x}")

    o = _off_for(segs, GAMEMODE_START)
    result[o:o + 4] = _b(GAMEMODE_START, syms["game_mode_start_trampoline"])
    print(f"start     {GAMEMODE_START:#010x} -> b {syms['game_mode_start_trampoline']:#x}")

    o = _off_for(segs, REFRESH)
    result[o:o + 4] = bytes.fromhex("c0035fd6")   # ret

    # SaveManager.SaveCharacter: capture the character object so the progress
    # reporter can read (and later upload) its experience/level.
    if "savechar_trampoline" in syms:
        o = _off_for(segs, SAVECHAR)
        result[o:o + 4] = _b(SAVECHAR, syms["savechar_trampoline"])
        print(f"savechar  {SAVECHAR:#010x} -> b {syms['savechar_trampoline']:#x}")

    out.write_bytes(bytes(result))
    print(f"injected {len(blob)} bytes at {STUB_VA:#x}")
    print(f"showdlg   {SHOWDLG:#x} -> b {tramp:#x}")
    print(f"signin    {ONSIGNIN:#x} -> b {entry:#x}")
    print(f"wrote {out}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("source", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--stub", type=Path,
                    default=Path(__file__).resolve().parent.parent / "device/stub/stub.bin")
    a = ap.parse_args()
    build(a.source, a.output, a.stub)