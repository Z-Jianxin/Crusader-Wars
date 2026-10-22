"""Validate native levy-view versus empty-context baseline/debuff/restore captures.

Use the fresh run's prefixed debug log, after performing each read in all three
states. This validates real captured output; it does not synthesize stat rows.
"""

import argparse
from decimal import Decimal
import json
from pathlib import Path


def frames(text, prefix):
    result = []
    active = None
    for raw in text.splitlines():
        line = raw.split("Running console command: ", 1)[-1].strip()
        if not line.startswith(prefix + "|"):
            continue
        fields = line.split("|")[1:]
        if fields == ["BEGIN"]:
            assert active is None, f"nested {prefix} frame"
            active = {}
        elif fields == ["END"]:
            assert active is not None, f"unmatched {prefix} END"
            result.append(active)
            active = None
        elif active is not None:
            key = "|".join(fields[:-1])
            assert key not in active, f"duplicate {prefix} key {key}"
            active[key] = fields[-1]
    assert active is None, f"incomplete {prefix} frame"
    return result


def validate(path, effects_log=None):
    text = path.read_text(encoding="utf-8-sig")
    effects = effects_log.read_text(encoding="utf-8-sig") if effects_log else text
    native = frames(text, "CCLEVYVIEW")
    probe = frames(text, "CCLEVYKEY")
    assert len(native) >= 3 and len(probe) >= 3, "need three complete reads of each view"
    native, probe = native[-3:], probe[-3:]
    assert "CCLEVYTEST|debuff_present" in effects, "modifier application not confirmed"
    assert "CCLEVYTEST|debuff_removed" in effects, "modifier cleanup not confirmed"
    assert "CCLEVYTEST|FAIL" not in effects, "modifier definition was unavailable"
    keys = ("base_damage", "base_toughness", "owner_damage", "owner_toughness")
    values = []
    for reference, candidate in zip(native, probe):
        assert reference["owner"] == candidate["owner"] == native[0]["owner"]
        assert reference["valid"] == candidate["empty_scope|valid"] == "no"
        row = {}
        for key in keys:
            actual = Decimal(reference[key])
            assert actual == Decimal(candidate["empty_scope|" + key]), key
            assert actual.is_finite() and actual > 0, key
            row[key] = actual
        values.append(row)
    before, debuff, restored = values
    assert before == restored, "levy values did not restore exactly"
    for key in ("base_damage", "base_toughness", "owner_damage"):
        assert before[key] == debuff[key], f"levy-only toughness changed {key}"
    assert before["base_toughness"] == 10
    assert debuff["owner_toughness"] == before["owner_toughness"] / 2, \
        "native -5 from base10 did not halve effective levy toughness"
    for key in keys:
        control = [Decimal(row["control|" + key]) for row in probe]
        assert len(set(control)) == 1, f"levy-only modifier changed MAA {key}"
    return {
        "passed": True,
        "owner": native[0]["owner"],
        "nativeAndEmptyContextAgree": True,
        "virtualTypeIsValid": False,
        "toughness": [str(row["owner_toughness"]) for row in values],
        "restoredExactly": True,
        "maaControlUnchanged": True,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--effects-log", type=Path,
                        help="fresh debug log for modifier markers when input is console_history.txt")
    args = parser.parse_args()
    print(json.dumps(validate(args.log, args.effects_log), indent=2))
