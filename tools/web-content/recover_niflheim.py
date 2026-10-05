#!/usr/bin/env python3
"""Hash-pinned instruction evidence for the Android 1.9.3 Niflheim run rules."""
import hashlib
import json
import struct
import disasm_powers as native

SHA = '529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d'
SPANS = {
    'total_packs': (0x2bd9d10, 0x2bd9d60),
    'pack_counter': (0x2bda540, 0x2bda580),
    'town_portal_on_final_packs': (0x2bda5f0, 0x2bda640),
    'random_pack_size': (0x2bdabf0, 0x2bdac2c),
    'final_pack_size': (0x2bdab90, 0x2bdac0c),
    'final_pack_spawn_call': (0x2bdbc80, 0x2bdbd1c),
    'exit_chest': (0x2bda64c, 0x2bda734),
    'open_chest_counts': (0x298f1f8, 0x298f2a0),
    'silver_for_finish': (0x2bdc7f0, 0x2bdc810),
}


def main():
    binary = native.SO.read_bytes()
    assert hashlib.sha256(binary).hexdigest() == SHA, 'Unreviewed binary'
    segments = native.load_segments(binary)
    evidence = {}
    for key, (start, end) in SPANS.items():
        evidence[key] = [f'{i.address:#x} {i.mnemonic} {i.op_str}'
                         for i in native.disassemble(binary, segments, start, end - start)]
    def check(key, value):
        assert any(value in line for line in evidence[key]), (key, value)
    check('total_packs', 'ldr x1, [x8, #0x2380]')
    check('total_packs', 'mov w1, #2')
    check('total_packs', 'bl #0x40af8a8')
    check('pack_counter', 'str w9, [x19, #0xd0]')
    check('town_portal_on_final_packs', 'bl #0x2bb2cc8')
    check('random_pack_size', 'bl #0x2bddb30')
    check('random_pack_size', 'fmov d0, #2.00000000')
    check('random_pack_size', 'fmov d1, #5.00000000')
    check('final_pack_size', 'ldr x1, [x8, #0x2418]')
    check('final_pack_size', 'add w0, w0, #1')
    check('final_pack_spawn_call', 'mov w5, wzr')
    check('final_pack_spawn_call', 'stp xzr, xzr, [sp, #0x60]')
    check('final_pack_spawn_call', 'ldp x6, x7, [sp, #0x60]')
    check('final_pack_spawn_call', 'bl #0x2a810ac')
    check('exit_chest', 'bl #0x2b5e04c')
    check('exit_chest', 'bl #0x298ee68')
    for low, high in (('#0x3c', '#0x46'), ('#0x64', '#0x7d')):
        check('open_chest_counts', f'mov w0, {low}')
        check('open_chest_counts', f'mov w1, {high}')
    chance_va = 0x1387678
    assert struct.unpack_from('<d', binary, native.va_to_off(segments, chance_va))[0] == 0.2
    check('silver_for_finish', 'mov w1, #0x1388')
    check('silver_for_finish', 'bl #0x2da9194')
    fields = native.field_offsets_by_class()
    assert fields['GameAttributes'][0x2380] == '_Num_Monster_Packs_k__BackingField'
    assert fields['NiflheimPortalGameMode'][0xd0] == '_TotalPacks_k__BackingField'
    result = {
        'baseline': 'Android 1.9.3 (507033)', 'binarySha256': SHA,
        'methods': {'InternalStart': '0x02BD9AC0',
                    'CheckIfPackShouldBeSpawned': '0x02BDA150',
                    'InternalFixedUpdate': '0x02BDAE04',
                    'AddSilverForFinish.MoveNext': '0x02BDC70C'},
        'reviewed': {
            'totalPacks': 'max(world attribute 133 Num_Monster_Packs, 2)',
            'packSize': 'GameWorld.GetRandomPackSize(2, 5) with fractional remainder carried across packs',
            'lastCombatPackSize': '1 + Area_Contains_More_Bosses (world attribute 2717); SpawnMonster receives canRollBoss=false and no forced rarity',
            'exitChest': 'After TownPortal creation, CalculateChance(0.2) gates a LootChest; Large when RangeInclusive(0,100) < 5, otherwise Medium',
            'chestBaseRolls': 'LootChest.OpenChest: Medium RangeInclusive(60,70), Large RangeInclusive(100,125)',
            'silverForFinish': 5000,
            'remainingRules': 'Other area modifiers, Champion/Rare rates and ranged/elemental filters need separate verification'},
        'evidence': evidence,
    }
    output = native.ROOT / 'tools/web-content/generated/niflheim_recovery.json'
    output.write_text(json.dumps(result, indent=2) + '\n')
    print('Verified Niflheim total packs, spawn counter, random pack call and finish silver.')


if __name__ == '__main__':
    main()
