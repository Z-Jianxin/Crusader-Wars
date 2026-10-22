"""Read native trial telemetry without controlling games or inventing outcome rows."""
import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from statistics import median


def inspect(path):
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    starts = re.findall(r"^CC_ACTUAL_TRIAL\|START\|(\d+)\|(\d+)\r?$", text, re.M)
    snapshots = sorted(set(re.findall(r"^CC_ACTUAL_TRIAL\|SNAPSHOT\|([^\r\n]+)", text, re.M)))
    expected = None if not starts else dict(zip(("attacker", "defender"), map(int, starts[-1])))
    samples = defaultdict(dict)
    for match in re.finditer(r"^CC_ACTUAL_TRIAL\|SAMPLE\|(\d+)\|(attacker|defender)\|([^|\r\n]+)\|(\d+)\|(\d+)\|(true|false)\r?$", text, re.M):
        tick, side, name, alive, kills, routing = match.groups()
        samples[int(tick)][name] = {"side": side, "alive": int(alive), "kills": int(kills), "routing": routing == "true"}
    complete = sorted(tick for tick, rows in samples.items()
                      if expected and Counter(row["side"] for row in rows.values()) == expected)
    current = samples[complete[-1]] if complete else {}
    totals = {}
    for side in ("attacker", "defender"):
        rows = [row for row in current.values() if row["side"] == side]
        totals[side] = {"formations": len(rows), "alive": sum(row["alive"] for row in rows),
                        "kills": sum(row["kills"] for row in rows), "routingFormations": sum(row["routing"] for row in rows)}
    orders = defaultdict(dict)
    for line in text.splitlines():
        fields = line.split("|")
        if len(fields) == 13 and fields[:2] == ["CC_ACTUAL_TRIAL", "ORDER"]:
            tick, name, action, target, x, z, tx, tz, distance, visible, completed = fields[2:]
            # Native logging may flush a partial final line; only completed rows count.
            if completed == "call-completed":
                orders[int(tick)][name] = {"action": action, "target": target,
                                          "x": float(x), "z": float(z), "distance": float(distance),
                                          "visible": visible == "true"}
    latest_orders = orders[max(orders)] if orders else {}
    distances = [row["distance"] for row in latest_orders.values()]
    return {
        "snapshots": snapshots, "expectedFormations": expected,
        "latestSampleSeconds": max(samples, default=None),
        "latestCompleteSampleSeconds": complete[-1] if complete else None,
        "firstKillSampleSeconds": next((tick for tick in complete if any(row["kills"] > 0 for row in samples[tick].values())), None),
        "latestCompleteTotals": totals if complete else None,
        "latestOrderSeconds": max(orders, default=None),
        "latestOrderActions": dict(Counter(row["action"] for row in latest_orders.values())),
        "latestOrderHiddenTargets": sum(not row["visible"] for row in latest_orders.values()),
        "latestOrderDistanceMetres": None if not distances else {"min": min(distances), "median": median(distances), "max": max(distances)},
        "errors": re.findall(r"^CC_ACTUAL_TRIAL\|ERROR\|([^\r\n]+)", text, re.M),
        "literalOutcomes": sorted(set(re.findall(r"^(Victory|Defeat)\r?$", text, re.M))),
        "completeReportCount": text.count("-----PRINT ENDED-----!!"),
        "nativeDismissalSeen": "Battle has finished" in text,
        "limit": "Telemetry only. No winner/casualty result is inferred or written.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    print(json.dumps(inspect(parser.parse_args().log), indent=2))
