"""Coverage and provenance checks for the all-passive native evidence inventory."""
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/web-content"))
import recover_passive_skills as recovery


class PassiveSkillRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = json.loads((ROOT / "tools/web-content/generated/passive_skill_recovery.json").read_text())
        cls.skills = {row["name"]: row for row in cls.document["skills"]}

    def test_every_class_pool_passive_has_an_explicit_row(self):
        pools = json.loads((ROOT / "tools/web-content/generated/power_pools.json").read_text())
        expected = {p["name"] for pool in pools.values() for p in pool["passive"]}
        self.assertEqual(set(self.skills), expected)
        self.assertEqual(len(expected), 42)

    def test_native_method_addresses_match_annotated_dump(self):
        indexed = [row for row in self.skills.values() if row["sourceAvailable"]]
        self.assertEqual(len(indexed), self.document["indexedImplementationCount"])
        self.assertEqual(len(indexed), 41)
        for row in indexed:
            for name in ("initialize", "apply", "remove"):
                method = row["methods"][name]
                self.assertIsNotNone(method, f"{row['name']} {name}")
                self.assertIsNotNone(method["nativeMethodSha256"], f"{row['name']} {name} hash")
                self.assertTrue(method["annotationMatchesNativeAddress"], f"{row['name']} {name} address")

    def test_trigger_hooks_are_indexed_for_special_passive_buffs(self):
        def hooks(name):
            return {hook["signature"] for hook in self.skills[name]["triggerHooks"]}
        self.assertTrue(any("OnPayload" in signature for signature in hooks("Overkill")))
        self.assertTrue(any("OnMeleeSwingHit" in signature for signature in hooks("MaceSpecialization")))
        self.assertTrue(any("Brain_OnSkillStarted" in signature for signature in hooks("Meditation")))
        self.assertTrue(any("BuffManager_OnBuffAdded" in signature for signature in hooks("Solitary")))
        self.assertTrue(any("Update(" in signature or "Stack(" in signature
                            for signature in hooks("Precision")))
        overkill_write = self.skills["Overkill"]["applyAttributeWrites"][0]
        self.assertEqual((overkill_write["attribute"], overkill_write["sourceField"]),
                         ("Overkill_Chance", "_OverkillChance"))
        force_field_write = self.skills["ForceField"]["applyAttributeWrites"][0]
        self.assertEqual((force_field_write["attribute"], force_field_write["sourceField"]),
                         ("ForceField_Bonus_Percent_Final", "_MoreForceField"))

    def test_native_field_stores_resolve_raw_offsets_without_dump_layout_guessing(self):
        self.assertTrue(all(not row["unresolvedRawFieldNames"] for row in self.skills.values()))
        self.assertEqual(self.skills["Overkill"]["resolvedRawFieldValues"], {"_OverkillChance": 1.0})
        self.assertEqual(self.skills["AccumulatingShadows"]["resolvedRawFieldValues"],
                         {"_ShadowBoltDamageIncrease": 0.2})
        self.assertEqual(self.skills["CheatDeath"]["resolvedRawFieldValues"],
                         {"_VitalityBonusPercent": 0.005})
        # The legacy direct-store values are coefficients/caps, not necessarily rank-1 values.
        overkill = self.skills["Overkill"]["initializerFormulas"]["_OverkillChance"]
        self.assertEqual((overkill["rank1"], overkill["perRank"], overkill["capMax"]), (0.4, 0.02, 1.0))
        vitality = self.skills["CheatDeath"]["initializerFormulas"]["_VitalityBonusPercent"]
        self.assertEqual((vitality["rank1"], vitality["perRank"]), (0.2, 0.005))
        evasive = self.skills["EvasiveManeuver"]
        self.assertNotIn("_TeleportCooldownReductionTime", evasive["initializerFormulas"])
        self.assertIn("_TeleportCooldownReductionTime", evasive["suspiciousInitializerValues"])
        dagger = self.skills["DaggerSpecialization"]
        self.assertFalse(dagger["sourceAvailable"])
        self.assertEqual(dagger["recoveryStatus"], "native_implementation_source_not_found")
        self.assertEqual(self.document["fullyBehaviorRecoveredCount"], 0)

    @unittest.skipUnless(recovery.native.SO.exists() and recovery.native.META.exists(), "local APK/native metadata required")
    def test_reproducible_binary_index(self):
        self.assertEqual(recovery.recover(), self.document)


if __name__ == "__main__":
    unittest.main()
