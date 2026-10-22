import json
from pathlib import Path
import tempfile
import unittest

from native_catalog import CatalogError, Node, build_catalog, write_catalog


class NativeCatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="native-catalog-test-")
        self.root = Path(self.temp.name)
        self.install = self.root / "install"
        self.game = self.install / "game"
        self.user = self.root / "user"
        self.ck3 = self.install / "binaries" / "ck3"
        self.write(self.game / "common/men_at_arms_types/types.txt", """
            @strength=22
            alpha={ type=heavy_infantry damage=@strength toughness=18 stack=100
                can_recruit={ culture={ has_innovation=innovation_tools } }
            }
            equipment={ type=siege_weapon fights_in_main_phase=no damage=1 toughness=12 }
        """)
        self.write(self.game / "common/men_at_arms_types/_men_at_arms_types.info",
                   "fights_in_main_phase=no # Default yes\n")
        self.write(self.game / "common/culture/cultures/cultures.txt",
                   "test_culture={heritage=heritage_test language=language_test}")
        self.write(self.game / "common/culture/pillars/pillars.txt", "heritage_test={type=heritage}")
        self.write(self.game / "common/culture/eras/eras.txt", "era_early={year=900}")
        self.write(self.game / "common/culture/innovations/innovations.txt",
                   "innovation_tools={culture_era=era_early unlock_maa=alpha potential={always=yes}}")
        self.configure([])

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def write(path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def configure(self, mods):
        self.write(self.user / "dlc_load.json", json.dumps({"disabled_dlcs": [], "enabled_mods": mods}))

    def add_mod(self, name, body, replace=None, filename="types.txt"):
        root = self.root / name
        self.write(root / "common/men_at_arms_types" / filename, body)
        descriptor = 'path="' + root.as_posix() + '"\n'
        if replace:
            descriptor += 'replace_path="' + replace + '"\n'
        self.write(self.user / "mod" / (name + ".mod"), descriptor)
        return "mod/" + name + ".mod"

    def test_native_semantics_macros_and_conditional_gates_are_preserved(self):
        catalog, _ = build_catalog(self.ck3, self.user)
        rows = {row["key"]: row for row in catalog["definitions"]}
        self.assertEqual((2, 1, 1), tuple(catalog["counts"][k] for k in ["definitions", "cultures", "heritages"]))
        self.assertEqual(22, rows["alpha"]["damage"])
        self.assertTrue(rows["alpha"]["fightsInMainPhase"])
        self.assertFalse(rows["equipment"]["fightsInMainPhase"])
        self.assertEqual(1, rows["equipment"]["damage"])  # Noncombatant is not inferred from zero damage.
        self.assertIn("has_innovation=innovation_tools", rows["alpha"]["recruitmentOrEraGatesRaw"])
        self.assertTrue(rows["alpha"]["conditionalEligibilityUnresolved"])
        self.assertEqual(900, rows["alpha"]["directUnlockInnovations"][0]["earliestEraYear"])
        self.assertEqual(1, catalog["nativeSemantics"]["defaultEvidenceLine"])
        self.assertEqual(64, len(rows["alpha"]["sourceSha256"]))

    def test_enabled_order_and_virtual_file_replacement_do_not_merge_removed_rows(self):
        first = self.add_mod("first", "alpha={type=archers damage=30}")
        second = self.add_mod("second", "beta={type=light_cavalry damage=12}")
        self.add_mod("disabled", "hidden={type=elephant_cavalry}")
        self.configure([first, second])
        catalog, _ = build_catalog(self.ck3, self.user)
        self.assertEqual(["beta"], [row["key"] for row in catalog["definitions"]])
        self.assertEqual(2, catalog["activeModCount"])
        self.configure([second, first])
        catalog, _ = build_catalog(self.ck3, self.user)
        self.assertEqual(["alpha"], [row["key"] for row in catalog["definitions"]])
        self.assertEqual("archers", catalog["definitions"][0]["category"])

    def test_replace_path_masks_native_types_and_accepts_relative_mod_paths(self):
        mod = self.add_mod("replacement", "replacement={type=camel_cavalry}",
                           replace="common/men_at_arms_types", filename="replacement.txt")
        self.write(self.user / mod, 'path="../replacement"\nreplace_path="common/men_at_arms_types"\n')
        self.configure([mod])
        catalog, _ = build_catalog(self.ck3, self.user)
        self.assertEqual(["replacement"], [row["key"] for row in catalog["definitions"]])

    def test_parser_respects_comments_quoted_braces_and_rejects_truncation(self):
        text = '\ufeff \n# ignored {\nunit={ label="braces } { and \\" quote" type=archers nested={value=2} }'
        fields = list(Node(text, 0, len(text)).entries())
        self.assertEqual(1, len(fields))
        self.assertEqual("unit", fields[0][0])
        self.assertEqual("archers", fields[0][1].one("type").scalar())
        broken = "unit={type=archers"
        with self.assertRaises(CatalogError):
            list(Node(broken, 0, len(broken)).entries())

    def test_conflicting_or_dynamic_values_are_explicitly_unresolved(self):
        self.write(self.game / "common/men_at_arms_types/types.txt", """
            unknown={ type=archers damage=@external toughness=10 toughness=20 fights_in_main_phase=dynamic }
            repeated={ type=pikemen stack=100 stack=100 }
        """)
        catalog, _ = build_catalog(self.ck3, self.user)
        rows = {row["key"]: row for row in catalog["definitions"]}
        self.assertIsNone(rows["unknown"]["damage"])
        self.assertIsNone(rows["unknown"]["toughness"])
        self.assertIsNone(rows["unknown"]["fightsInMainPhase"])
        self.assertTrue(rows["unknown"]["unresolvedNativeSemantics"])
        self.assertEqual("@external", rows["unknown"]["unresolvedNumericFields"]["damage"])
        self.assertEqual(100, rows["repeated"]["stack"])

    def test_missing_and_duplicate_active_descriptors_fail_without_guessing(self):
        self.configure(["mod/missing.mod"])
        with self.assertRaises(OSError):
            build_catalog(self.ck3, self.user)
        mod = self.add_mod("same", "only={type=archers}")
        self.configure([mod, mod])
        with self.assertRaises(CatalogError):
            build_catalog(self.ck3, self.user)

    def test_output_is_new_external_and_never_overwrites_live_input(self):
        catalog, protected = build_catalog(self.ck3, self.user)
        for target in [self.game / "report.json", self.user / "report.json"]:
            with self.assertRaises(CatalogError):
                write_catalog(catalog, target, protected)
            self.assertFalse(target.exists())
        output = self.root / "new-export"
        path = write_catalog(catalog, output, protected)
        original = path.read_bytes()
        with self.assertRaises(CatalogError):
            write_catalog(catalog, output, protected)
        self.assertEqual(original, path.read_bytes())
        self.assertEqual("native-catalog.json", path.name)


if __name__ == "__main__":
    unittest.main()
