#!/usr/bin/env python3
"""Reproduce reviewed Android 1.9.3 affix-rarity evidence; fail on a different binary."""
import hashlib
import json
from capstone import Cs, CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN
import disasm_powers as native


def main():
    data = native.SO.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != '529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d':
        raise ValueError('Unreviewed native binary; review addresses and formula before exporting')
    segments = native.load_segments(data)
    md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
    spans = {
        'inputs': (0x2ca2828, 0x2ca28ec),
        'curve': (0x2ca2dbc, 0x2ca2dd8),
        'low_rarity': (0x2ca2f54, 0x2ca30b8),
        'previous_highest': (0x2ca3154, 0x2ca3360),
        'top_rarities': (0x2ca3464, 0x2ca3514),
    }
    evidence = {}
    for name, (start, end) in spans.items():
        off = native.va_to_off(segments, start)
        raw = data[off:off + end - start]
        evidence[name] = {'start': hex(start), 'endExclusive': hex(end),
            'sha256': hashlib.sha256(raw).hexdigest(),
            'instructions': [f'{i.address:#x} {i.mnemonic} {i.op_str}'.rstrip() for i in md.disasm(raw, start)]}
    for name, instruction in [('top_rarities', 'fmov d14, #3.00000000'),
                              ('top_rarities', 'fmov d14, #4.50000000'),
                              ('previous_highest', 'fmov d1, #2.25000000'),
                              ('low_rarity', 'fmov d0, #10.00000000')]:
        assert any(instruction in line for line in evidence[name]['instructions'])
    constants = {hex(a): native.read_const(data, segments, a, 8) for a in
        [0x1388268, 0x1388468, 0x1387368, 0x13870b0, 0x13874d0, 0x1387ab0,
         0x13879e0, 0x1387b88, 0x1387918, 0x1387890, 0x13879d8, 0x1387878,
         0x1387e70, 0x1386dd0, 0x1386fe0]}
    meta, _ = native.method_map()
    max_method = next(m for m in meta['methodDefinitions'] if int(m['virtualAddress'], 16) == 0x40af878)
    assert max_method['name'] == '_ZN6System4Math3MaxEdd'
    out = native.ROOT / 'tools/web-content/generated/affix_rarity_recovery.json'
    out.write_text(json.dumps({'baseline': 'Android 1.9.3 (507033)', 'binarySha256': digest,
        'method': 'ItemGenerator.InternalInitializeAffixRarityPool', 'address': '0x02CA2468',
        'constants': constants, 'evidence': evidence, 'rounding': 'none (double weights)',
        'curves': {'C': [1000, .22, 1], 'B': [800, .4, 1], 'A': [700, .6, 1],
            'AA': [600, .65, 1], 'AAA': [500, .6, 1.4], 'AAAA': [400, .6, 1.9],
            'AAAAA': [225, .5, 3], 'S': [150, .6, 4.5]},
        'formula': 'base * (1 + .01*(100*MF)*(coefficient*factor)/((100*MF)*slope+divisor*coefficient*factor))',
        'lowRarities': {'F/E': 'base/(1+MF)', 'D': 'base*(1+MF*(factor>=10?.1:1))'},
        'priorRoll': {'belowHighest': 'weight/(1+.025*affixNumber)',
            'atOrAboveHighest': 'weight*(1+.02*rarity*affixNumber)',
            'highestAtLeastDAndBelow': 'weight/max(abs(rarity-highest)*2.25,1)'},
        'normalCreateItem': 'initializes once at affixNumber=1, highest=null; GenerateRandomAffix receives allowReinitializingRarityPool=false',
        'zeroFactorExtension': 'saturated C–S bonus=0; F/E/D branches unchanged'}, indent=2) + '\n')
    print('Verified native evidence:', out.relative_to(native.ROOT))

if __name__ == '__main__':
    main()
