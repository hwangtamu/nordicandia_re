#!/usr/bin/env python3
"""Patch Android libil2cpp.so: hook *Offline slot-expansion methods to sync to server.

Hooks:
  SkillGrid.ExpandSkillSlotOffline(SkillGrid*, SkillSlotType, int cost)  0x252A3C8
    -> POST /api/character/expand-skill-slots {characterId, expandType, opalCost}
  InventoryGrid.ExpandPotionSlotOffline(InventoryGrid*, int cost)         0x250EA90
    -> POST /api/character/expand-potion-slots {characterId, opalCost}

The client only has Offline variants which never call the server. Without this
hook, purchased slots vanish on next login (server re-sends old GameModeAccount).

Cave: 0x3459000 (RX). State: 0x5DE8400 (RW).
"""
import argparse
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

from patch_android_email_login import _read_symbols, _segments, _off_for, _b, check_cave_fit
from patch_android_realtime import _extend_rw_memsz

def tool(name):
    path = shutil.which(name)
    if path:
        return path
    for directory in ('/opt/homebrew/opt/llvm/bin', '/usr/local/opt/llvm/bin'):
        path = Path(directory) / name
        if path.exists():
            return str(path)
    raise RuntimeError(f'{name} is required to build the slot-expand stub')

# Real VAs resolved at runtime via il2cpp (Frida), NOT Il2CppInspector: the
# inspector-derived constants were +0x4000 off (the old skill target 0x25263C8
# is an epilogue, and the old potion target 0x250AA90 is a different function).
EXPAND_SKILL_SLOT = 0x252A3C8
EXPAND_SKILL_SLOT_RESUME = 0x252A3CC
EXPAND_POTION_SLOT = 0x250EA90
EXPAND_POTION_SLOT_RESUME = 0x250EA94
UNLOCK_COMBAT_PET = 0x02E3DF40
UNLOCK_COMBAT_PET_RESUME = 0x02E3DF44
# CombatPetMinion.set_Level/set_Experience setters are never called on exp gain;
# CombatPetMinion.SetPetParameters(this) is (pet at [this+0x2B0]).
COMBAT_PET_SET_PARAMS = 0x2A661D4
COMBAT_PET_SET_PARAMS_RESUME = 0x2A661D8
# Aesir offering: the client applies it through the offline path
# (WindowAesirOffering.MakeOfferingOffline) and never calls MakeOffering, so we
# capture the size from PromptOfferingPurchase and POST the offering ourselves.
PROMPT_OFFERING = 0x255EC54
PROMPT_OFFERING_RESUME = 0x255EC58
MAKE_OFFERING = 0x255F2BC
MAKE_OFFERING_RESUME = 0x255F2C0

STUB_VA, STUB_END = 0x3459000, 0x345C9AB
STATE_VA = 0x5DE8400  # .bss start (from linker script); end computed from ELF
ROOT = Path(__file__).resolve().parent.parent

def _bss_end(elf: Path) -> int:
    """Read .bss section end address from ELF section headers."""
    import struct
    data = elf.read_bytes()
    if data[:4] != b"\x7fELF":
        raise ValueError(f"{elf} is not an ELF file")
    shoff = struct.unpack_from("<Q", data, 0x28)[0]
    shentsize, shnum, shstrndx = struct.unpack_from("<HHH", data, 0x3A)
    # Get section header string table
    o = shoff + shstrndx * shentsize
    str_off = struct.unpack_from("<Q", data, o + 24)[0]
    for i in range(shnum):
        o = shoff + i * shentsize
        name_idx, typ, flags, addr, off, size = struct.unpack_from("<IIQQQQ", data, o)
        # Get section name
        name_end = data.index(b"\x00", str_off + name_idx)
        name = data[str_off + name_idx:name_end].decode()
        if name == ".bss":
            # Page-align up
            end = addr + size
            return (end + 0xFFF) & ~0xFFF
    raise ValueError("No .bss section found in ELF")

# We need the il2cpp API addresses; they resolve dynamically in the stub,
# but the stub references them as extern. We'll use --defsym to provide them.
# Actually, the stub uses il2cpp_domain_get etc. via dynamic lookup in refresh_auth,
# but the extern declarations need addresses. Let's see what skill-rank does:
# It uses TARGETS with get_trained_rank VA, and il2cpp_* are resolved via syms.
# For slot-expand, we don't need fixed VAs for il2cpp APIs because refresh_auth
# finds them dynamically via metadata. But the linker needs them defined.
# We'll define them as 0 and the stub will resolve dynamically? No, that won't work.
#
# Actually, looking at skill_rank_stub.c, it declares:
#   extern ptr il2cpp_domain_get(void);
# And the patcher provides them via TARGETS dict which includes il2cpp_* from syms.
# The syms come from _read_symbols(source) where source is the original .so.
# So we need to find the il2cpp API VAs in the .so.
#
# For now, we'll use the same approach: read syms from the source .so.

# Link-time resume addresses, passed to the stub via --defsym so its trampolines
# can use a PC-relative `b RESUME` (ASLR-safe).  An absolute `.quad RESUME` +
# `br x16` is wrong in a shared object: the trampoline would jump to the raw
# link-time VA (e.g. pc=0x2e3df44) instead of libbase+VA and fault with SEGV_ACCERR.
TARGETS = {
    'EXPAND_SKILL_SLOT_RESUME': EXPAND_SKILL_SLOT_RESUME,
    'EXPAND_POTION_SLOT_RESUME': EXPAND_POTION_SLOT_RESUME,
    'UNLOCK_COMBAT_PET_RESUME': UNLOCK_COMBAT_PET_RESUME,
    'COMBAT_PET_SET_PARAMS_RESUME': COMBAT_PET_SET_PARAMS_RESUME,
    'PROMPT_OFFERING_RESUME': PROMPT_OFFERING_RESUME,
    'MAKE_OFFERING_RESUME': MAKE_OFFERING_RESUME,
}

def build_stub(source, directory):
    syms = _read_symbols(source)
    targets = dict(TARGETS)
    # il2cpp_* symbols from the source .so
    targets.update({k: v for k, v in syms.items() if k.startswith('il2cpp_')})
    stem = directory / 'slot_expand_stub'
    subprocess.run([tool('clang'), '-target', 'aarch64-linux-gnu', '-Os', '-ffreestanding',
                    '-ffixed-x18', '-fno-stack-protector', '-fno-pic', '-mno-outline-atomics',
                    '-c', str(ROOT / 'device/stub/slot_expand_stub.c'),
                    '-o', str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('ld.lld'), '-T', str(ROOT / 'device/stub/slot_expand_stub.ld'),
                    *[f'--defsym={k}={v:#x}' for k, v in targets.items()],
                    '-o', str(stem.with_suffix('.elf')), str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('llvm-objcopy'), '-O', 'binary', '--only-section=.text',
                    '--only-section=.rodata', str(stem.with_suffix('.elf')),
                    str(stem.with_suffix('.bin'))], check=True)
    blob = stem.with_suffix('.bin').read_bytes()
    elf_syms = _read_symbols(stem.with_suffix('.elf'))
    bss_end = _bss_end(stem.with_suffix('.elf'))
    print(f'Stub .bss ends at {bss_end:#x} (page-aligned)')
    return blob, elf_syms, bss_end

def apply(data, blob, syms, bss_end):
    result = bytearray(data)
    segs = _segments(data)
    if len(blob) > STUB_END - STUB_VA:
        raise ValueError('slot-expand stub exceeds its reserved code cave')
    check_cave_fit(STUB_VA, len(blob), 'slot_expand')
    
    # Verify hook entry points exist in syms
    for name in ('hook_expand_skill_slot', 'hook_expand_potion_slot', 'hook_unlock_combat_pet', 'hook_combat_pet_set_params', 'hook_prompt_offering', 'hook_make_offering'):
        if name not in syms:
            raise ValueError(f'{name} not found in stub symbols')
        if not STUB_VA <= syms[name] < STUB_VA + len(blob):
            raise ValueError(f'{name} lies outside the compiled stub')

    # Place the blob in the cave.  Its trampolines branch straight back to the
    # retail resume addresses with PC-relative `b RESUME`, resolved at link time
    # from the --defsym values in TARGETS, so nothing needs runtime patching.
    off = _off_for(segs, STUB_VA)
    result[off:off+len(blob)] = blob

    # Install hooks: overwrite first instruction of each target with branch to stub
    for target_va, hook_name in [
        (EXPAND_SKILL_SLOT, 'hook_expand_skill_slot'),
        (EXPAND_POTION_SLOT, 'hook_expand_potion_slot'),
        (UNLOCK_COMBAT_PET, 'hook_unlock_combat_pet'),
        (COMBAT_PET_SET_PARAMS, 'hook_combat_pet_set_params'),
        (PROMPT_OFFERING, 'hook_prompt_offering'),
        (MAKE_OFFERING, 'hook_make_offering'),
    ]:
        off = _off_for(segs, target_va)
        hook_va = syms[hook_name]
        result[off:off+4] = _b(target_va, hook_va)
        print(f'Hooked {target_va:#x} -> {hook_name} at {hook_va:#x}')

    # Extend RW for .bss
    _extend_rw_memsz(result, STATE_VA, bss_end)

    return bytes(result)

def build(source, output, artifacts=None):
    source, output = Path(source), Path(output)
    if source.resolve() == output.resolve():
        raise ValueError('Use a separate output path')
    with tempfile.TemporaryDirectory(prefix='nord-slotexpand-') as work:
        directory = Path(work)
        blob, syms, bss_end = build_stub(source, directory)
        patched = apply(source.read_bytes(), blob, syms, bss_end)
        output.write_bytes(patched)
        if artifacts:
            artifacts = Path(artifacts); artifacts.mkdir(parents=True, exist_ok=True)
            for item in directory.iterdir():
                shutil.copy2(item, artifacts / item.name)
        print(f'Injected slot-expand sync stub ({len(blob)} bytes) at {STUB_VA:#x}')

if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('source', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--artifacts', type=Path)
    args = p.parse_args()
    build(args.source, args.output, args.artifacts)
