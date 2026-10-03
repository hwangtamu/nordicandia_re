"""Golden checks against the manually reviewed ARM64 constants and call targets."""
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'tools/web-content'))
import recover_power_parameters as recovery


class PowerParameterRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = json.loads((ROOT / 'tools/web-content/generated/power_parameter_recovery.json').read_text())
        cls.powers = cls.document['powers']

    def test_all_previously_empty_initializers_are_classified(self):
        previous = json.loads((ROOT / 'tools/web-content/generated/power_values.json').read_text())
        self.assertEqual({k for k, v in previous.items() if not v}, set(self.powers))
        statuses = [p['status'] for p in self.powers.values()]
        self.assertEqual((statuses.count('rank_formulas'), statuses.count('runtime_inputs'), statuses.count('inherited')), (11, 4, 4))
        self.assertFalse(self.document['runtimeIntegrated'])

    def test_simd_lanes_and_paired_scalar_store(self):
        # (rank 1, rank 11): exercise both SIMD lanes, whose scales can differ.
        expected = {
            'TreasureHunter': [(0.30, 0.50), (0.10, 0.20)],
            'FireArmor': [(0.50, 1.50), (0.10, 0.20)],
            'ColdArmor': [(0.50, 1.50), (0.10, 0.20)],
            'LightningArmor': [(0.50, 1.50), (0.10, 0.20)],
            'Pillaging': [(0.50, 1.50), (0.20, 0.30)],
            'MasterSummoner': [(0.10, 0.20), (0.05, 0.10)],
            'VileTouch': [(0.20, 0.30), (0.60, 0.80)],
            'RepelMagic': [(0.03, 0.04), (0.03, 0.04)],
        }
        for name, pairs in expected.items():
            self.assertEqual(len(self.powers[name]['fields']), len(pairs))
            for field, (rank1, rank11) in zip(self.powers[name]['fields'].values(), pairs):
                self.assertAlmostEqual(field['rank1'], rank1, msg=name)
                self.assertAlmostEqual(field['rank1'] + 10 * field['perRank'], rank11, msg=name)

    def test_attribute_formulas_caps_and_origins(self):
        for name in ('Solitary', 'Fork'):
            formula, = self.powers[name]['attributes'].values()
            self.assertEqual((formula['rank1'], formula['perRank'], formula['origin']), (0.1, 0.005, 3))
        poison, double_crit = self.powers['DeadlyPoison']['attributes'].values()
        self.assertEqual((poison['rank1'], double_crit['rank1']), (0.15, 0.8))
        for formula in (poison, double_crit):
            self.assertEqual((formula['perRank'], formula['origin'], formula['capMax']), (0.01, 4, 1.0))
            self.assertEqual(min(formula['capMax'], formula['rank1'] + 149 * formula['perRank']), 1.0)

    def test_inheritance_and_runtime_sources_are_not_zero_filled(self):
        self.assertEqual(self.powers['KibuSmallLightningNova']['delegatesTo'], 'Nova')
        self.assertEqual(self.powers['UniqueItemFireballOnHit']['delegatesTo'], 'ProjectileSkill')
        mana = self.powers['ManaArrowsAbility']['attributes']
        self.assertEqual(mana['Power_Num_Projectiles']['value'], 1)
        self.assertEqual(mana['MaxStackAmount']['field'], '_NumArrowCharges')
        self.assertEqual(mana['CurrentStackAmount']['field'], '_NumArrowCharges')
        self.assertEqual(self.powers['Spitfire']['fields']['_WeaponDamageMult']['kind'], 'user_attribute')
        self.assertEqual(self.powers['Wander']['attributes']['Base_Cooldown']['arguments'], [1, 6])

    @unittest.skipUnless(recovery.native.SO.exists() and recovery.native.META.exists(), 'local APK/native metadata required')
    def test_native_reproduction_and_version_guard(self):
        self.assertEqual(recovery.recover(), self.document)
        with patch.dict(recovery.EXPECTED_HASHES, {'TreasureHunter': 'invalid-build'}):
            with self.assertRaisesRegex(ValueError, 'initializer changed'):
                recovery.recover()


if __name__ == '__main__':
    unittest.main()
