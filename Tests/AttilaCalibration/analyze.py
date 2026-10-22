"""Summarize a task-owned Attila calibration directory without assuming outcomes."""
import argparse
import csv
import json
import math
import statistics
from pathlib import Path


def analyze(directory: Path) -> dict:
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    matches = {item["Label"]: item for item in manifest["matches"]}
    lane_indices = {label: i for i, label in enumerate(matches)}
    runs = {}
    with (directory / "battle.csv").open(encoding="utf-8-sig", newline="") as source:
        for row in csv.reader(source):
            if len(row) < 3:
                continue
            run = runs.setdefault(row[0], {"complete": False, "started": False, "errors": [], "pairs": {}})
            kind = row[2]
            if kind == "ERROR":
                run["errors"].append(",".join(row[3:]))
            elif kind == "COMPLETE":
                run["complete"] = True
                run["end_reason"] = row[4] if len(row) > 4 else "unknown"
            elif kind == "START":
                if run["started"]:
                    run["errors"].append("Duplicate START: possible run ID collision")
                run["started"] = True
            elif kind == "UNIT_TYPES" and len(row) >= 7:
                label = row[4]
                if label not in matches:
                    run["errors"].append("Unknown pair " + label)
                    continue
                pair = run["pairs"].setdefault(label, {})
                pair["unit_types"] = (row[5], row[6])
            elif kind in {"ATTACK_ORDER", "POSITION"} and len(row) >= 9:
                label = row[4]
                if label not in matches:
                    run["errors"].append("Unknown position pair " + label)
                    continue
                pair = run["pairs"].setdefault(label, {})
                coordinates = [float(value) for value in row[5:9]]
                if not all(math.isfinite(value) for value in coordinates):
                    run["errors"].append("Nonfinite position for " + label)
                    continue
                pair.setdefault("positions", []).append({"kind": kind, "coordinates": coordinates,
                    "left_alive": pair.get("latest", {}).get("left_alive", 1),
                    "right_alive": pair.get("latest", {}).get("right_alive", 1)})
            elif kind in {"INITIAL", "SAMPLE", "PAIR_DONE", "FINAL"} and len(row) >= 9:
                label = row[4]
                if label not in matches:
                    run["errors"].append("Unknown pair " + label)
                    continue
                pair = run["pairs"].setdefault(label, {})
                values = {"seconds": int(row[3]), "left_alive": int(row[5]), "right_alive": int(row[6]),
                          "left_kills": int(row[7]), "right_kills": int(row[8])}
                if kind == "INITIAL":
                    pair["initial"] = values
                if kind == "PAIR_DONE":
                    pair["completed"] = values
                if kind == "FINAL":
                    pair["final"] = values
                pair["latest"] = values
    summaries = []
    for run_id, run in runs.items():
        grouped = {}
        if manifest.get("nativeRosterControl"):
            run["errors"].append("Native-roster structural control: no stat treatments are active")
        if not run["started"]:
            run["errors"].append("Missing START")
        if not run["complete"]:
            run["errors"].append("Incomplete run: missing COMPLETE")
        for label, item in matches.items():
            if label not in run["pairs"]:
                run["errors"].append("Missing expected pair " + label)
                continue
            samples = run["pairs"][label]
            item = matches[label]
            initial = samples.get("initial")
            final = samples.get("completed", samples.get("latest"))
            if not initial or not final:
                run["errors"].append("Missing initial/final sample for " + label)
                continue
            if initial["left_alive"] != initial["right_alive"] or initial["left_alive"] != manifest.get("expectedMen", 60):
                run["errors"].append("Unexpected unit scaling/initial size for " + label)
            if initial["left_kills"] != 0 or initial["right_kills"] != 0:
                run["errors"].append("Combat began before initial sample for " + label)
            if samples.get("unit_types") != (item["LeftKey"], item["RightKey"]):
                run["errors"].append("Missing or incorrect unit types for " + label)
            if "final" not in samples:
                run["errors"].append("Missing FINAL sample for " + label)
            # Out-of-pair kills identify wandering/cross-lane combat and invalidate the sample.
            # Include the final snapshot after PAIR_DONE: halted survivors can still be attacked.
            contaminated = any(value["left_kills"] != initial["right_alive"] - value["right_alive"] or
                               value["right_kills"] != initial["left_alive"] - value["left_alive"]
                               for value in (final, samples["latest"]))
            treated = "right_alive" if item["Mirrored"] else "left_alive"
            control = "left_alive" if item["Mirrored"] else "right_alive"
            placement_valid, outside_bounds, lane_departure = True, False, False
            if manifest.get("nativeScenario"):
                spacing = manifest.get("laneSpacingMetres", 80)
                half_map = manifest.get("playableAreaMetres", 1924) / 2
                lane = (lane_indices[label] - (len(matches) - 1) / 2) * spacing
                positions = samples.get("positions", [])
                initial_position = next((p for p in positions if p["kind"] == "ATTACK_ORDER"), None)
                placement_valid = bool(initial_position) and all(
                    math.hypot(initial_position["coordinates"][side * 2] - (-35 if side == 0 else 35),
                               initial_position["coordinates"][side * 2 + 1] - lane) <= 20 for side in (0, 1))
                for position in positions:
                    for side, alive in ((0, "left_alive"), (1, "right_alive")):
                        if position[alive] == 0:
                            continue
                        x, z = position["coordinates"][side * 2:side * 2 + 2]
                        outside_bounds |= abs(x) > half_map or abs(z) > half_map
                        lane_departure |= abs(z - lane) > spacing / 2
            result = {"pair": label, "treated_survivors": final[treated], "control_survivors": final[control],
                      "survivor_margin": final[treated] - final[control], "seconds": final["seconds"],
                      "completed": "completed" in samples, "contaminated": contaminated,
                      "placement_valid": placement_valid, "outside_bounds": outside_bounds, "lane_departure": lane_departure,
                      "casualties_observed": final["left_alive"] < initial["left_alive"] or final["right_alive"] < initial["right_alive"]}
            grouped.setdefault(item["Case"], []).append(result)
        cases = {}
        eligible = run["complete"] and run["started"] and not run["errors"]
        for name, trials in grouped.items():
            valid = [t for t in trials if eligible and not t["contaminated"] and t["casualties_observed"]
                     and t["placement_valid"] and not t["outside_bounds"] and not t["lane_departure"]]
            cases[name] = {"trials": trials, "valid_trials": len(valid),
                           "median_survivor_margin": statistics.median(t["survivor_margin"] for t in valid) if valid else None,
                           "median_seconds": statistics.median(t["seconds"] for t in valid) if valid else None}
        summaries.append({"run": run_id, "complete": run["complete"], "end_reason": run.get("end_reason"),
                          "eligible": eligible, "errors": run["errors"], "case_summaries": cases})
    return {"suite": manifest["suite"], "base_unit": manifest["baseUnit"], "runs": summaries,
            "interpretation": "Descriptive pilot results only. Positive margins suggest stronger units; they do not establish exact CK3 equivalence or individual mounted HP semantics."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    arguments = parser.parse_args()
    print(json.dumps(analyze(arguments.directory), indent=2))
