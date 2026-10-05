#!/usr/bin/env python3
"""Export reviewed native rarity-roll spans; refuse an unreviewed Android binary.

This records disassembly evidence, not a general control-flow decompiler. The
branch interpretation and remaining world-pool limitations are documented in
docs/web/MONSTER_SPAWN_RECOVERY.md.
"""
import hashlib
import json
import disasm_powers as native

SHA = '529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d'
SPANS = {
    'argument_registers': (0x2a810d0, 0x2a81100),
    'forced_rarity_and_denominators': (0x2a81378, 0x2a81420),
    'unique_gate_and_roll': (0x2a81b0c, 0x2a81b70),
    'rare_and_champion_gates_and_rolls': (0x2a820b4, 0x2a8216c),
    'definition_rarity_and_type_predicate': (0x2a83dd0, 0x2a83f48),
    'unique_attribute_read': (0x2989630, 0x2989650),
    'champion_attribute_read': (0x29896c4, 0x29896e4),
    'multiplier_one': (0x29891a0, 0x29891a4),
    'multiplier_additions': (0x2989be4, 0x2989bec),
    'dungeon_spawn_call': (0x2989e18, 0x2989e6c),
}


def main():
    data = native.SO.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA, 'Unreviewed binary'
    segments = native.load_segments(data)
    evidence = {}
    for key, (start, end) in SPANS.items():
        evidence[key] = dict(start=hex(start), endExclusive=hex(end), instructions=[
            f'{i.address:#x} {i.mnemonic} {i.op_str}'
            for i in native.disassemble(data, segments, start, end-start)])

    def check(key, instruction):
        assert any(line.endswith(instruction) for line in evidence[key]['instructions']), (key, instruction)

    for constant in ('0x4069000000000000', '0x4059000000000000', '0x4049000000000000'):
        check('forced_rarity_and_denominators', f'mov x8, #{constant}')
    check('unique_gate_and_roll', 'ccmp w8, #1, #0, lt')
    check('unique_gate_and_roll', 'mov w8, #4')
    check('rare_and_champion_gates_and_rolls', 'fmov d0, #25.00000000')
    check('rare_and_champion_gates_and_rolls', 'fmov d0, #10.00000000')
    check('rare_and_champion_gates_and_rolls', 'mov w8, #2')
    check('rare_and_champion_gates_and_rolls', 'mov w8, #1')
    check('multiplier_one', 'fmov d13, #1.00000000')
    check('multiplier_additions', 'fadd d9, d9, d13')
    check('multiplier_additions', 'fadd d10, d10, d13')
    check('dungeon_spawn_call', 'fmov d1, d10')
    check('dungeon_spawn_call', 'fmov d2, #1.00000000')
    check('dungeon_spawn_call', 'fmov d3, d9')
    check('dungeon_spawn_call', 'bl #0x2a810ac')
    fields = native.field_offsets_by_class()['GameAttributes']
    ids = json.loads((native.ROOT/'tools/web-content/generated/attribute_ids.json').read_text())
    assert fields[0x88] == f"_{ids['12']}_k__BackingField"
    assert fields[0x90] == f"_{ids['164']}_k__BackingField"
    assert ids['12'] == 'UniqueMonster_Find_Bonus_Percent'
    assert ids['164'] == 'ChampionMonster_Find_Bonus_Percent'
    result = dict(binarySha256=SHA, baseline='Android 1.9.3 (507033)', evidence=evidence,
        reviewedRules=dict(order=['Unique', 'Rare', 'Champion'],
            conditionalProbabilities={'Unique': 'uniqueSpawnMult / 200',
                'Rare': 'rareSpawnMult / 100', 'Champion': 'champSpawnMult / 50'},
            eligibility={'Unique': 'level >= 50 OR tier >= 2',
                'Rare': 'level >= 25 OR tier >= 2', 'Champion': 'level >= 10 OR tier >= 2'},
            dungeonMultipliers={'champion': '1 + attribute 164', 'rare': '1', 'unique': '1 + attribute 12'},
            enum={'Normal': 0, 'Champion': 1, 'Rare': 2, 'Minion': 3, 'Unique': 4, 'Hireling': 5, 'Boss': 6},
            definitionFilter='AvailableRarities.Contains(rarity) AND Type.IsOfType(selectedType)',
            caveat='These are conditional rolls; failed definition selection can reduce realized spawns.'))
    path = native.ROOT/'tools/web-content/generated/monster_spawn_recovery.json'
    path.write_text(json.dumps(result, indent=2)+'\n')
    print('Verified native spawn constants, branch spans and dungeon find multipliers.')


if __name__ == '__main__':
    main()
