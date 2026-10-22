"""Reject calibration evidence that did not execute the expected isolated trials."""
import json
import tempfile
import unittest
from pathlib import Path

from analyze import analyze


class CalibrationAnalysisTests(unittest.TestCase):
    def summarize(self, lines, extra_matches=(), native_roster_control=False, native_scenario=False):
        with tempfile.TemporaryDirectory(prefix="cc-calibration-analysis-") as temporary:
            directory = Path(temporary)
            matches = [{"Label": "damage2_r1", "Case": "damage2", "Mirrored": False,
                        "LeftKey": "strong", "RightKey": "baseline"}, *extra_matches]
            (directory / "manifest.json").write_text(json.dumps({
                "suite": "infantry", "baseUnit": "native", "matches": matches,
                "nativeRosterControl": native_roster_control, "nativeScenario": native_scenario}), encoding="utf-8")
            (directory / "battle.csv").write_text("\n".join("1,infantry," + line for line in lines), encoding="utf-8")
            return analyze(directory)["runs"][0]

    @staticmethod
    def complete_lines():
        return ["UNIT_TYPES,0,damage2_r1,strong,baseline", "START,0",
                "INITIAL,0,damage2_r1,60,60,0,0,false,false",
                "PAIR_DONE,30,damage2_r1,40,0,60,20,false,false",
                "FINAL,30,damage2_r1,40,0,60,20,false,false",
                "COMPLETE,30,all_pairs_complete"]

    def test_completed_expected_trial_is_eligible(self):
        run = self.summarize(self.complete_lines())
        self.assertTrue(run["eligible"])
        self.assertEqual(run["case_summaries"]["damage2"]["valid_trials"], 1)
        self.assertEqual(run["case_summaries"]["damage2"]["median_survivor_margin"], 40)

    def test_incomplete_run_does_not_contribute_valid_trials(self):
        run = self.summarize(self.complete_lines()[:-1])
        self.assertFalse(run["eligible"])
        self.assertEqual(run["case_summaries"]["damage2"]["valid_trials"], 0)

    def test_missing_expected_lane_invalidates_run(self):
        missing = {"Label": "damage2_r2", "Case": "damage2", "Mirrored": True,
                   "LeftKey": "baseline", "RightKey": "strong"}
        run = self.summarize(self.complete_lines(), [missing])
        self.assertFalse(run["eligible"])
        self.assertIn("Missing expected pair damage2_r2", run["errors"])

    def test_wrong_unit_types_and_initial_scaling_invalidate_run(self):
        for old, new in [("strong,baseline", "baseline,baseline"), ("60,60,0,0", "30,30,0,0")]:
            run = self.summarize([line.replace(old, new) for line in self.complete_lines()])
            self.assertFalse(run["eligible"])
            self.assertEqual(run["case_summaries"]["damage2"]["valid_trials"], 0)

    def test_contamination_after_pair_completion_is_detected(self):
        lines = self.complete_lines()
        lines[-2] = "FINAL,45,damage2_r1,35,0,65,20,false,false"
        run = self.summarize(lines)
        self.assertTrue(run["eligible"])
        self.assertTrue(run["case_summaries"]["damage2"]["trials"][0]["contaminated"])
        self.assertEqual(run["case_summaries"]["damage2"]["valid_trials"], 0)

    def test_structural_native_roster_control_is_not_stat_evidence(self):
        run = self.summarize(self.complete_lines(), native_roster_control=True)
        self.assertFalse(run["eligible"])
        self.assertEqual(run["case_summaries"]["damage2"]["valid_trials"], 0)

    def test_idle_timeout_is_not_valid_casualty_evidence(self):
        lines = self.complete_lines()[:3] + [
            "FINAL,300,damage2_r1,60,60,0,0,false,false", "COMPLETE,300,300_second_timeout"]
        run = self.summarize(lines)
        self.assertTrue(run["eligible"])
        case = run["case_summaries"]["damage2"]
        self.assertFalse(case["trials"][0]["casualties_observed"])
        self.assertEqual(case["valid_trials"], 0)
        self.assertIsNone(case["median_survivor_margin"])

    def test_native_placement_and_lane_bounds_gate_combat_evidence(self):
        valid = self.complete_lines()
        valid.insert(3, "ATTACK_ORDER,0,damage2_r1,-35,0,35,0,-35,0,35,0")
        self.assertEqual(self.summarize(valid, native_scenario=True)["case_summaries"]["damage2"]["valid_trials"], 1)
        for record, flag in [
            ("ATTACK_ORDER,0,damage2_r1,-200,0,35,0,-35,0,35,0", "placement_valid"),
            ("POSITION,5,damage2_r1,-35,1000,35,0,-35,0,35,0", "outside_bounds"),
            ("POSITION,5,damage2_r1,-35,50,35,0,-35,0,35,0", "lane_departure")]:
            rows = self.complete_lines()
            rows.insert(3, record if flag == "placement_valid" else valid[3])
            if flag != "placement_valid": rows.insert(4, record)
            case = self.summarize(rows, native_scenario=True)["case_summaries"]["damage2"]
            self.assertEqual(case["valid_trials"], 0)
            self.assertEqual(case["trials"][0][flag], flag != "placement_valid")


if __name__ == "__main__":
    unittest.main()
