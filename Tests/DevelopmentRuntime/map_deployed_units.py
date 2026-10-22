"""Bind an archived, actually launched XML to an isolated replay's source identities.

This is read-only evidence analysis. It cannot generate battle results or alter saves.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path
from xml.etree import ElementTree as ET


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_json(path, **kwargs):
    return json.loads(path.read_text(encoding="utf-8-sig"), **kwargs)


def inventory(path):
    result = {}
    for unit in ET.parse(path).getroot().iter("unit"):
        name = unit.attrib["script_name"]
        if name in result:
            raise ValueError(f"Duplicate XML script: {name}")
        result[name] = {
            "deployedBodies": int(unit.attrib["num_soldiers"]),
            "unitKey": unit.find("unit_type").attrib["type"],
        }
    return result


def main():
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument("--launched-xml", required=True, type=Path)
    args.add_argument("--replay", required=True, type=Path)
    args.add_argument("--additional-profiles", action="append", type=Path, default=[],
                      help="Optional independently recorded profiles; exact generated hashes are still required.")
    args.add_argument("--out", required=True, type=Path)
    options = args.parse_args()
    if options.out.exists():
        raise ValueError("Output must be new; existing evidence is never overwritten.")
    replay = options.replay
    summary = read_json(replay / "result.json")
    if not summary.get("success") or not summary.get("SaveEvidenceValidated"):
        raise ValueError("Need a successful, save-bound replay.")
    expected_xml = replay / "data/battle files/script/tut_tutorial_battle/tut_battle.xml"
    deployed = inventory(options.launched_xml)
    expected = inventory(expected_xml)
    if deployed.keys() != expected.keys() or any(
            deployed[name]["deployedBodies"] != expected[name]["deployedBodies"] for name in deployed):
        raise ValueError("Launched script names/counts do not exactly match this replay.")

    # Preserve .NET's round-trip numeric literals for its generated-key algorithm.
    profile_path = replay / "profiles.json"
    by_key = {}
    for path in [profile_path, *options.additional_profiles]:
        for profile in read_json(path, parse_float=str, parse_int=str):
            signature = "\n".join(profile[key] for key in
                                  ("Id", "BaseUnitKey", "DamageMultiplier", "HitPointMultiplier"))
            key = "cc_sf_" + hashlib.sha256(signature.encode()).hexdigest()[:24]
            if key in by_key and by_key[key] != profile:
                raise ValueError(f"Conflicting generated profile: {key}")
            by_key[key] = profile

    units = {}
    for army in read_json(replay / "armies.json"):
        for unit in army["units"]:
            units[(str(army["ID"]), str(unit["UniqueID"]))] = unit
    rows = []
    for name, actual in deployed.items():
        army_match = re.search(r"_(attacker|defender)_army(\d+)_TYPE", name)
        unit_match = re.search(r"_(\d+)_CULTURE", name)
        if not army_match or not unit_match:
            raise ValueError(f"Unrecognized actual script identity: {name}")
        side, army_id = army_match.groups()
        unit = units[(army_id, unit_match.group(1))]
        if actual["deployedBodies"] != unit["soldiers"]:
            raise ValueError(f"Replay object/XML body count mismatch: {name}")
        profile = by_key.get(actual["unitKey"])
        if actual["unitKey"].startswith("cc_sf_") and profile is None:
            raise ValueError(f"No exact generated profile: {name}")
        same_key = actual["unitKey"] == expected[name]["unitKey"]
        if not same_key:
            # The inherited visual mapper can select different fallback levy templates.
            # Never silently borrow a different MAA identity or changed stat multipliers.
            prior = by_key.get(expected[name]["unitKey"])
            prefix = f"{side}:{army_id}:Levy:"
            if unit["SourceIdentity"] is not None or unit["type"] != "Levy" or not profile or not prior \
                    or not profile["Id"].startswith(prefix) or not prior["Id"].startswith(prefix) \
                    or any(profile[k] != prior[k] for k in ("DamageMultiplier", "HitPointMultiplier")):
                raise ValueError(f"Unexplained deployed/replay profile mismatch: {name}")
        rows.append({
            "scriptName": name, "luaReference": "UNIT" + name,
            "side": side, "armyId": army_id, **actual,
            "regimentType": unit["type"], "regimentName": unit["name"],
            "sourceIdentity": unit["SourceIdentity"],
            "sourceOwnerId": unit["SourceOwnerId"],
            "sourceOriginProvinceId": unit["SourceOriginProvinceId"],
            "statProfile": profile,
            "replayUnitKey": expected[name]["unitKey"], "sameReplayTemplate": same_key,
            "statPath": "direct-generated" if profile else "legacy-siege-or-unconverted",
        })
    evidence = {
        "snapshot": summary["snapshotId"], "combatId": summary["CombatID"],
        "input": read_json(replay / "inputs.json"),
        "launchedXmlSha256": digest(options.launched_xml),
        "replayXmlSha256": digest(expected_xml), "profilesSha256": digest(profile_path),
        "exactScriptAndBodyCountMatch": True,
        "exactDeployedInventoryMatch": deployed == expected,
        "differentLevyTemplateCount": sum(not row["sameReplayTemplate"] for row in rows),
        "additionalProfileSources": [{"path": str(path), "sha256": digest(path)}
                                     for path in options.additional_profiles],
        "unitCount": len(rows),
        "actualResults": False, "syntheticResults": False,
        "purpose": "Exact deployed-unit/source binding only; no casualty outcome is inferred.",
        "units": rows,
    }
    options.out.write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Bound {len(rows)} actual XML formations for {summary['snapshotId']}: {options.out}")


if __name__ == "__main__":
    main()
