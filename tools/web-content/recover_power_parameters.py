#!/usr/bin/env python3
"""Recover/classify the 19 initializers missed by disasm_powers.py.

This is an audited, version-gated supplement, not a general ARM64 emulator. Rank
coefficients are read from ELF constants; symbolic runtime sources remain symbolic.
No values are substituted into power_values.json (which only supports constants).
Run with .tools/web-assets-venv/bin/python tools/web-content/recover_power_parameters.py.
"""
from __future__ import annotations
import argparse
import bisect
import hashlib
import json
import re
import struct
from pathlib import Path

from capstone import Cs, CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN
import disasm_powers as native

ROOT = native.ROOT
# Exact initializer bytes reviewed for this APK. Refuse to apply recipes to another build.
EXPECTED_HASHES = {'TreasureHunter': '5295ee4ad9856a8078ec4d8f5342dfe7c7cb6eb9b7383ad8699a06c36491925d',
 'FireArmor': 'a9252adde8af06de720c682d85eb59bf77e6c5d01864bb7e04cd5a54c5d01cb5',
 'ColdArmor': 'ca084796b0649ef5a92e5c67d37e2fa7ed794be1d70a4df8c1f85d72b7fd7419',
 'LightningArmor': 'ff1cbb894e7c5d1d2e84b4979f8c59550a030e66b8283d675166700fdee03377',
 'Wander': 'a0cdc791bc1f25d1fb16db7692e6bcf28568fd49f8e7b82c7a850fa88c3803a3',
 'ManaArrowsAbility': 'd003149ea57ecabe895b184bff1c5babee74b48f39dbd8b52d19584a1ac267a2',
 'DeadlyPoison': '48b0921e4977c28a79d9fbcae3b4ff664e4b8b0f3d9a6b78bbdbc8075d8ade9b',
 'Solitary': '203656d127432e88efe3898b14347ba348c0fa81ab8ab8cf032c71c5da43f4bb',
 'Fork': 'b08baf3c420bece6ae72898ad97b06df8d56e3be758543d87e3223c3143dbc26',
 'RepelMagic': '5997005769fad0058cf22b3a4054e3c9d9a1b743cf586d71c6b8cfdac566c372',
 'UniqueItemFireballOnHit': '77887f7a9dd4501520f46bb7a18716bad48f6666c3690619b905d39d547cf8a3',
 'ShootExplodingFireArrow': '4545b701d06ecc995e47738fa8ec3261684eecf048a74b13b2f57332590ebaa5',
 'Spitfire': '8d5230938fd80c693715dce67cae84eafda50098e221bb38875f5f9df7b96283',
 'Whirl': '371f2a6df17e3e36aff890e25bf4e122526279c5cbbf9b0a38fbd20ee535ce97',
 'UniqueItemThrowHammerOnHit': 'db1a97c42425c49054c2dfeb31503fe87bd279a42473d88d84098bd51c1d2afa',
 'Pillaging': '5ffdfa5584bf868b74e24c41b94876ec16fa879ab06dc199a87bfb2ec1167a00',
 'MasterSummoner': '41cfa036bdf63fc1fdebbde3489dc8ad77e97fd52028fe4224b68aff93812daf',
 'VileTouch': '135dbbd5f1446a7d6f657360dfd62097dc9589bb3f982847a061c2b60dbd8996',
 'KibuSmallLightningNova': 'd2c34e76d360bcd6bb7d271912fd5c98ec28a9abf0d4bb4b42917e39bd8061cc'}

PAIRED_FIELDS = {
    'TreasureHunter': ['_MagicFindIncrease', '_ItemQuantityIncrease'],
    'FireArmor': ['_ResistanceBonus', '_DamageTakenAsElement'],
    'ColdArmor': ['_ResistanceBonus', '_DamageTakenAsElement'],
    'LightningArmor': ['_ResistanceBonus', '_DamageTakenAsElement'],
    'Pillaging': ['_MinionMagicFindIncrease', '_AdditionalIronDropChance'],
    'MasterSummoner': ['_MinionLifeBonus', '_MinionDamageBonus'],
    'VileTouch': ['_MorePoisonDamage', '_PoisonChanceOnHit'],
    'RepelMagic': ['_IncreasedMaximumResistances', '_IncreasedMaximumPhysReduction'],
}
INHERITED = {
    'UniqueItemFireballOnHit': 'ProjectileSkill',
    'UniqueItemThrowHammerOnHit': 'ProjectileSkill',
    'Whirl': 'Power',
    'KibuSmallLightningNova': 'Nova',
}


def locate(meta, name):
    suffix = f'{len(name)}{name}33InternalInitializePowerParametersEi'
    hits = [m for m in meta['methodDefinitions'] if m['name'].endswith(suffix)]
    if len(hits) != 1:
        raise ValueError(f'{name}: expected one exact initializer, found {len(hits)}')
    return int(hits[0]['virtualAddress'], 16)


def constant_loads(insns, data, segments):
    """Only literal ADRP + LDR d/q loads, tagged by native instruction address."""
    pages, loads = {}, []
    for ins in insns:
        if ins.mnemonic == 'adrp':
            reg, value = ins.op_str.split(', ')
            pages[reg] = int(value[1:], 0)
        match = re.fullmatch(r'([dq])\d+, \[(x\d+), #(0x[0-9a-f]+)\]', ins.op_str)
        if ins.mnemonic == 'ldr' and match and match[2] in pages:
            address = pages[match[2]] + int(match[3], 0)
            off = native.va_to_off(segments, address)
            if off is None:
                raise ValueError(f'unmapped constant {address:#x}')
            size = 16 if match[1] == 'q' else 8
            loads.append({'instruction': hex(ins.address), 'address': hex(address),
                          'values': list(struct.unpack_from('<' + 'd' * (size // 8), data, off))})
        # Do not retain an ADRP page after that integer register is overwritten.
        if ins.mnemonic != 'adrp' and ins.op_str:
            dest = ins.op_str.split(',')[0]
            if dest in pages and ins.mnemonic not in ('str', 'stp', 'cmp', 'tst'):
                pages.pop(dest)
    return loads


def linear(rank1, per_rank, **extra):
    return {'kind': 'linear_rank', 'rank1': rank1, 'perRank': per_rank,
            'expression': 'rank1 + perRank * (rank - 1)', **extra}


def recover():
    data = native.SO.read_bytes()
    segments = native.load_segments(data)
    meta, _ = native.method_map()
    addresses = sorted(set(int(m['virtualAddress'], 16) for m in meta['methodDefinitions']))
    md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
    attrs = native.load_game_attributes()
    powers = {}
    for name, expected_hash in EXPECTED_HASHES.items():
        start = locate(meta, name)
        end = addresses[bisect.bisect_right(addresses, start)]
        off = native.va_to_off(segments, start)
        code = data[off:off + end - start]
        digest = hashlib.sha256(code).hexdigest()
        if digest != expected_hash:
            raise ValueError(f'{name}: initializer changed; review native code before updating recipe')
        insns = list(md.disasm(code, start))
        loads = constant_loads(insns, data, segments)
        entry = {'method': 'InternalInitializePowerParameters', 'address': hex(start),
                 'methodSha256': digest, 'constantLoads': loads,
                 'scope': 'Own initializer only; base initializer, constructors, buffs and execution may add parameters.'}
        if name in PAIRED_FIELDS:
            assert len(insns) == 18 and len(loads) == 2
            slopes, bases = loads[0]['values'], loads[1]['values']
            if name == 'RepelMagic':
                slopes *= 2
                bases *= 2
                assert insns[14].op_str == 'd0, d0, [x20, #0x120]'
            else:
                assert insns[11].op_str == 'v0.2d, v1.2d, v0.d[0]'
                assert insns[13].op_str == 'v0.2d, v0.2d, v1.2d'
                assert insns[14].op_str == 'q0, [x20, #0x120]'
            entry.update(status='rank_formulas', fields={
                field: linear(bases[i], slopes[i], offset=hex(0x120 + i * 8))
                for i, field in enumerate(PAIRED_FIELDS[name])})
        elif name in ('Solitary', 'Fork'):
            assert len(loads) == 2
            offset = 0x15e8 if name == 'Solitary' else 0x24f8
            entry.update(status='rank_formulas', attributes={attrs[offset]:
                linear(loads[1]['values'][0], loads[0]['values'][0], origin=3,
                       attributeStaticOffset=hex(offset))})
        elif name == 'DeadlyPoison':
            assert len(loads) == 3
            slope, poison, crit = (x['values'][0] for x in loads)
            entry.update(status='rank_formulas', attributes={
                attrs[offset]: linear(base, slope, capMax=1.0, origin=4,
                                     attributeStaticOffset=hex(offset))
                for offset, base in [(0x6b8, poison), (0x6c0, crit)]})
        elif name in INHERITED:
            assert len(insns) == 2 and insns[-1].mnemonic == 'b'
            target = int(insns[-1].op_str[1:], 16)
            assert target == locate(meta, INHERITED[name])
            entry.update(status='inherited', delegatesTo=INHERITED[name], targetAddress=hex(target))
        elif name == 'ManaArrowsAbility':
            entry.update(status='runtime_inputs', attributes={
                attrs[0x1460]: {'kind': 'constant', 'value': 1, 'origin': 3},
                attrs[0x328]: {'kind': 'field', 'field': '_NumArrowCharges', 'offset': '0x144', 'origin': 3},
                attrs[0x2b8]: {'kind': 'field', 'field': '_NumArrowCharges', 'offset': '0x144', 'origin': 3}},
                note='Weapon multiplier, charges and mana gains come from constructor arguments; do not invent a fixed rank table.')
        elif name == 'Wander':
            entry.update(status='runtime_inputs', attributes={attrs[0x1340]:
                {'kind': 'call', 'function': 'UnityEngine.Random.Range(int,int)', 'arguments': [1, 6], 'origin': 3}},
                note='Cooldown is sampled at initialization, not a missing constant.')
        elif name == 'ShootExplodingFireArrow':
            entry.update(status='runtime_inputs', fields={'_WeaponDamageMult':
                {'kind': 'user_attribute', 'attribute': attrs[0x1f68], 'offset': '0x138', 'condition': 'User != null'}},
                attributes={attrs[0x1578]: {'kind': 'expression', 'expression': '10 * Constants.Default_Base_Movement_Speed', 'origin': 3},
                            attrs[0x1488]: {'kind': 'constant', 'value': 0, 'origin': 3},
                            attrs[0x1490]: {'kind': 'constant', 'value': 0, 'origin': 3}})
        elif name == 'Spitfire':
            entry.update(status='runtime_inputs', fields={
                '_WeaponDamageMult': {'kind': 'user_attribute', 'attribute': attrs[0x1f80], 'offset': '0x130', 'condition': 'User != null'},
                'OpenDelay': {'kind': 'call', 'function': 'PowerScript.GetActionSpeed (virtual dispatch)', 'offset': '0x120', 'condition': 'User != null'}})
        else:
            raise ValueError(f'missing recipe: {name}')
        entry['instructions'] = [f'{i.address:#x} {i.mnemonic} {i.op_str}'.rstrip() for i in insns]
        powers[name] = entry
    return {'schemaVersion': 1,
            'source': {'binary': str(native.SO.relative_to(ROOT)), 'sha256': hashlib.sha256(data).hexdigest(),
                       'fieldAndAttributeNames': 'steam_analysis/dump.cs; native offsets retained for cross-version review'},
            'formulaSemantics': 'rank1 + perRank * (rank - 1); apply capMax afterwards when present. Stored units, not UI percentages.',
            'runtimeIntegrated': False, 'powers': powers}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'tools/web-content/generated/power_parameter_recovery.json')
    args = parser.parse_args()
    result = recover()
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    counts = {}
    for entry in result['powers'].values():
        counts[entry['status']] = counts.get(entry['status'], 0) + 1
    print(json.dumps(counts), '->', args.output)


if __name__ == '__main__':
    main()
