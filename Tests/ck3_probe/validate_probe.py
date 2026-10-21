"""Validate real engine output from the baseline/boost/restore probe.

This deliberately consumes a captured CK3 debug log, not synthetic getter data.
Usage: python Tests/ck3_probe/validate_probe.py path/to/prefixed_debug.log
"""

import argparse
from decimal import Decimal
from pathlib import Path


def validate(path):
    records = {}
    complete = False
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        marker = "Running console command: CCPROBE|"
        if marker not in line:
            continue
        record = line.split(marker, 1)[1].strip()
        if record == "BEGIN":
            records = {}
            complete = False
        elif record == "END":
            complete = True
        elif "|" in record:
            key, value = record.split("|", 1)
            assert key not in records, f"Duplicate record: {key}"
            records[key] = value
    assert complete, "No complete real-engine probe frame found"
    for key in ("regiment", "owner", "serial_integer"):
        assert int(records[key]) > 0, key
    for key in ("base_damage", "base_toughness", "advantage_coefficient",
                "knight_damage_per_prowess", "knight_toughness_per_prowess"):
        assert Decimal(records[key]) > 0, key
    for stat in ("damage", "toughness", "pursuit", "screen",
                 "knight_effectiveness", "commander_advantage"):
        baseline = Decimal(records[f"baseline_{stat}"])
        boosted = Decimal(records[f"boosted_{stat}"])
        restored = Decimal(records[f"restored_{stat}"])
        assert baseline == restored, f"{stat}: baseline {baseline} != restored {restored}"
        if stat in ("damage", "toughness", "knight_effectiveness", "commander_advantage"):
            assert boosted > baseline, f"{stat}: boost was not reflected ({baseline}, {boosted})"
        print(f"{stat}: {baseline} -> {boosted} -> {restored}")
    print("PASS: native effective getters respond to owner modifiers and restore exactly.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("debug_log", type=Path)
    validate(parser.parse_args().debug_log)
