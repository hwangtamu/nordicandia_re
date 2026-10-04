#!/usr/bin/env python3
"""Record reproducible E03/E04 evidence from the hash-pinned Android arm64 binary.
Stores reviewed instruction spans and assertions, not decompiler FMOV guesses.
"""
import hashlib
import json
import disasm_powers as native

SHA = '529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d'
SPANS = {
    'steel_output': (0x2c83a48, 0x2c83ab4),
    'steel_return': (0x2c842b0, 0x2c842dc),
    'steel_overshoot_return': (0x2c83fc8, 0x2c84098),
    'steel_iron_selector': (0x2c863e4, 0x2c86508),
    'titan_output': (0x2c84610, 0x2c846a4),
    'titan_return': (0x2c84e4c, 0x2c84e70),
    'titan_ore_selector': (0x2c86798, 0x2c868bc),
    'titan_steel_selector': (0x2c868bc, 0x2c869e0),
    'highest_rarity': (0x2c865b4, 0x2c86668),
    'fill_open_slot': (0x2c8a6dc, 0x2c8a8b8),
    'same_generation_predicate': (0x2c8cdd8, 0x2c8ce40),
    'coefficient_table': (0x2c85704, 0x2c858c8),
}

def main():
    data = native.SO.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA, 'Unreviewed binary'
    segments = native.load_segments(data)
    spans = {}
    for key, (start, end) in SPANS.items():
        instructions = [f'{i.address:#x} {i.mnemonic} {i.op_str}'
                        for i in native.disassemble(data, segments, start, end-start)]
        spans[key] = dict(start=hex(start), endExclusive=hex(end), instructions=instructions)
    def check(key, text):
        assert any(text in i for i in spans[key]['instructions']), (key,text)
    check('steel_output', 'mov w27, w0')
    check('steel_return', 'mov w0, w27')
    check('titan_output', 'fmov d1, #10.00000000')
    check('titan_output', 'mov x8, #0x4059000000000000') # double 100
    check('titan_return', 'mov w0, w26')
    check('steel_iron_selector', '#0x21c')
    check('titan_ore_selector', '#0x220')
    check('titan_steel_selector', '#0x224')
    assert native.read_const(data, segments, 0x1387140, 8) == 300
    affixes = json.loads((native.ROOT/'gamedata_decrypted/Affixes.json').read_text())
    slots = [a for a in affixes if a['IntegerId'] in (1076,1077)]
    assert [(a['GenerationType'],a['AttributeSpecifierDefinitionList'][0]['AttributeId']) for a in slots] == [(0,389),(1,390)]
    result = dict(binarySha256=SHA, baseline='Android 1.9.3 (507033)',
        evidence=spans, openSlotDefinitions=slots,
        rules=dict(steel='min(floor(ironStacks/300), floor(sum(coefficient(highestRarity(essence)))))',
            titansteel='min(floor(titaniumOreStacks/10), floor(steelStacks/100))',
            zeroOutput='No mutation; Max(1, output) in native only constructs UI consumption hints',
            steelSelection='Essences ascending by highest rarity; return low-value essences from overshoot (epsilon 1e-5)',
            essenceCoefficients={2:.05,3:.1,4:.15,5:.2,6:.25,7:.5,8:1,9:3,10:10,11:50}))
    path = native.ROOT/'tools/web-content/generated/crafting_slots_smelting_recovery.json'
    path.write_text(json.dumps(result, indent=2)+'\n')
    print('Verified smelting return registers, material selectors and open-slot definitions.')

if __name__=='__main__': main()
