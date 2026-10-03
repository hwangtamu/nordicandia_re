#!/usr/bin/env python3
"""Export reviewed ARM64 evidence for loot luck, poison, fork and offline KPM.
Cpp2IL's annotated FMOV immediates are unreliable here; decode actual ELF bytes.
This report records evidence, not a general decompiler or runtime configuration.
"""
import hashlib
import json
from capstone import Cs, CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN
import disasm_powers as native

ROOT = native.ROOT


def main():
    data = native.SO.read_bytes()
    segments = native.load_segments(data)
    md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
    spans = {
        'rarity_inputs': (0x2ca0808, 0x2ca0884),
        'rarity_type_switch': (0x2ca0bcc, 0x2ca0c08),
        'rarity_curve_and_rounding': (0x2ca0c88, 0x2ca0e2c),
        'poison_duration_and_dps': (0x2c24ab8, 0x2c24b28),
        'fork_first_child': (0x2a03534, 0x2a03574),
        'fork_second_child': (0x2a03594, 0x2a035e4),
        'offline_rate': (0x2678cb4, 0x2678d64),
    }
    evidence = {}
    for name, (start, end) in spans.items():
        off = native.va_to_off(segments, start)
        raw = data[off:off + end - start]
        evidence[name] = {'start': hex(start), 'endExclusive': hex(end),
                          'sha256': hashlib.sha256(raw).hexdigest(),
                          'instructions': [f'{i.address:#x} {i.mnemonic} {i.op_str}'.rstrip()
                                           for i in md.disasm(raw, start)]}
    constants = {name: {'address': hex(address), 'value': native.read_const(data, segments, address, 8)}
                 for name, address in {'uniqueCoefficient': 0x13879d8, 'setCoefficient': 0x1387878,
                     'percentScale': 0x1386fe0, 'setSlope': 0x13874d0,
                     'poisonDamageFactor': 0x1387678, 'offlineRateCap': 0x13872d8}.items()}
    # Guard the known source before asserting formula semantics. A new APK needs review.
    assert [v['value'] for v in constants.values()] == [225, 150, 0.01, 0.6, 0.2, 45]
    def has(group, text):
        assert any(text in line for line in evidence[group]['instructions']), (group, text)
    has('rarity_type_switch', 'fmov d9, #3.00000000')
    has('rarity_type_switch', 'fmov d9, #4.50000000')
    has('rarity_curve_and_rounding', 'fdiv d9, d0, d11')
    has('poison_duration_and_dps', 'fmov d0, #1.00000000')
    has('fork_first_child', 'fmov d4, #0.50000000')
    has('fork_second_child', 'fmov d4, #0.50000000')
    has('offline_rate', 'fmov d1, #1.50000000')
    has('offline_rate', 'fmov d1, #10.00000000')
    result = {'schemaVersion': 1, 'binarySha256': hashlib.sha256(data).hexdigest(),
              'constants': constants, 'evidence': evidence,
              'rarityFormula': {'p': '100 * magicFind', 'f': 'magicFindFactorMultiplier',
                  'unique': 'roundToEven(baseUnique * (1 + .01*p*225*f/(.5*p+3*225*f)))',
                  'set': 'roundToEven(baseSet * (1 + .01*p*150*f/(.6*p+4.5*150*f)))',
                  'normal': 'roundToEven(baseNormal / (1 + .01*p*f/(p+f)))'},
              'poison': {'duration': 1, 'dps': 'HitPayload.TotalDamage * .2',
                         'tick': 'Tick_Damage_Per_Second * deltaTime; DamageType.Poison'},
              'fork': {'children': 2, 'damageMultiplier': .5, 'canFork': False, 'canChain': False,
                       'webCollisionGeometry': 'Provisional: nearby targets instead of moving projectiles'},
              'offline': {'attributeStaticOffset': '0x200', 'attributeId': 565,
                          'attribute': 'Offline_Battle_Efficiency_Multiplier',
                          'formula': 'truncate(efficiency * clamp(1.5 * secondHighestReachedTier, 10, 45))',
                          'tierSource': 'Character.GetSecondHighestReachedWorldCheckpoint @ 0x02b7d9c4',
                          'runtimeIntegrated': False},
              'itemQuantityRoundingRecovered': False}
    output = ROOT / 'tools/web-content/generated/luck_hit_rules.json'
    output.write_text(json.dumps(result, indent=2) + '\n')
    print('Verified constants and instruction guards:', output)


if __name__ == '__main__':
    main()
