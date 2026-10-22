"""Build an active CK3 type/culture catalog without running either game.

The catalog describes native definitions and conditional gate evidence. It does
not evaluate recruitment triggers or certify that every culture can recruit each
type. All game/mod inputs are read-only; output must be new and outside them.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation
import hashlib
import json
import math
from pathlib import Path
import re
import sys
from typing import Iterable

MAX_FILE_BYTES = 16 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_FILES = 10000
MAX_MODS = 2048
MAX_DEPTH = 96
TOKEN = re.compile(r'[\s\ufeff]+|#[^\r\n]*|"(?:\\.|[^"\\])*"|[{}=]|[^\s\ufeff{}=#"]+')
SPECIAL = re.compile(r'"(?:\\.|[^"\\])*"|#[^\r\n]*|[{}]')


class CatalogError(ValueError):
    pass


@dataclass(frozen=True)
class Node:
    text: str
    start: int
    end: int
    block: bool = False

    def raw(self) -> str:
        return self.text[self.start:self.end]

    def scalar(self) -> str:
        if self.block:
            raise CatalogError("Expected a scalar, not a block")
        value = self.raw()
        return re.sub(r'\\(["\\])', r'\1', value[1:-1]) if value.startswith('"') else value

    def entries(self):
        cursor = self.start + int(self.block)
        end = self.end - int(self.block)
        count = 0
        while (token := next_token(self.text, cursor, end)) is not None:
            count += 1
            if count > 100000:
                raise CatalogError("Native field count exceeds bounds")
            first = read_value(self.text, token.start(), end)
            cursor = first.end
            following = next_token(self.text, cursor, end)
            if not first.block and following is not None and following.group() == "=":
                assigned = next_token(self.text, following.end(), end)
                if assigned is None:
                    raise CatalogError("Missing assigned native value")
                value = read_value(self.text, assigned.start(), end)
                yield first.scalar(), value
                cursor = value.end
            else:
                yield None, first

    def all(self, key: str) -> list[Node]:
        return [value for name, value in self.entries() if name == key]

    def one(self, key: str, required: bool = False) -> Node | None:
        values = self.all(key)
        if len(values) > 1:
            raise CatalogError(f"Duplicate native field: {key}")
        if not values:
            if required:
                raise CatalogError(f"Missing native field: {key}")
            return None
        return values[0]


def next_token(text: str, start: int, end: int):
    while start < end:
        match = TOKEN.match(text, start, end)
        if match is None:
            raise CatalogError(f"Unrecognized native syntax at character {start}")
        token = match.group()
        if not all(c.isspace() or c == "\ufeff" for c in token) and not token.startswith("#"):
            return match
        start = match.end()
    return None


def read_value(text: str, start: int, end: int) -> Node:
    token = next_token(text, start, end)
    if token is None or token.group() in ("}", "="):
        raise CatalogError("Missing or invalid native value")
    if token.group() != "{":
        return Node(text, token.start(), token.end())
    depth = 0
    for match in SPECIAL.finditer(text, token.start(), end):
        if match.group() == "{":
            depth += 1
            if depth > MAX_DEPTH:
                raise CatalogError("Native block nesting exceeds bounds")
        elif match.group() == "}":
            depth -= 1
            if depth == 0:
                return Node(text, token.start(), match.end(), True)
    raise CatalogError("Unterminated native block")


def scalar_fields(node: Node) -> tuple[dict[str, str], dict[str, list[str]]]:
    values: dict[str, list[str]] = defaultdict(list)
    for key, value in node.entries():
        if key is not None and not value.block:
            values[key].append(value.scalar())
    return ({key: rows[-1] for key, rows in values.items()},
            {key: rows for key, rows in values.items() if len(rows) > 1})


@dataclass(frozen=True)
class SourceRoot:
    root: Path
    replace_paths: tuple[str, ...] = ()


class Inputs:
    def __init__(self):
        self.files: dict[str, dict] = {}
        self.total_bytes = 0

    def read(self, path: Path, maximum: int = MAX_FILE_BYTES) -> str:
        path = path.resolve()
        before = path.stat()
        if before.st_size > maximum:
            raise CatalogError(f"Input exceeds size limit: {path}")
        data = path.read_bytes()
        after = path.stat()
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise CatalogError(f"Input changed during read: {path}")
        identity = str(path)
        if identity not in self.files:
            self.total_bytes += len(data)
            if len(self.files) >= MAX_FILES or self.total_bytes > MAX_TOTAL_BYTES:
                raise CatalogError("Native input inventory exceeds bounds")
        self.files[identity] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        return data.decode("utf-8-sig")

    def digest(self, path: Path) -> str:
        return self.files[str(path.resolve())]["sha256"]


def active_roots(ck3: Path, user: Path, inputs: Inputs):
    installation = ck3.resolve().parent.parent
    game = installation / "game"
    if not (game / "common/men_at_arms_types").is_dir():
        raise CatalogError("CK3 executable must identify an installation with game/common/men_at_arms_types")
    config = user / "dlc_load.json"
    settings = json.loads(inputs.read(config, 4 * 1024 * 1024))
    enabled = settings.get("enabled_mods")
    if not isinstance(enabled, list) or len(enabled) > MAX_MODS or any(not isinstance(x, str) for x in enabled):
        raise CatalogError("enabled_mods must be a bounded array of descriptor paths")
    roots = [SourceRoot(game)]
    descriptors = []
    seen = set()
    for enabled_path in enabled:
        descriptor = Path(enabled_path)
        descriptor = (descriptor if descriptor.is_absolute() else user / descriptor).resolve()
        if descriptor in seen:
            raise CatalogError(f"Duplicate enabled descriptor: {descriptor}")
        seen.add(descriptor)
        text = inputs.read(descriptor, 1024 * 1024)
        node = Node(text, 0, len(text))
        declared = node.one("path")
        if declared is None:
            raise CatalogError(f"Enabled mod lacks an inspectable unpacked path: {descriptor}")
        root = Path(declared.scalar())
        root = (root if root.is_absolute() else user / root).resolve()
        if not root.is_dir():
            raise CatalogError(f"Enabled mod root is missing: {root}")
        replaces = tuple(v.scalar().replace("\\", "/").strip("/") for v in node.all("replace_path"))
        roots.append(SourceRoot(root, replaces))
        descriptors.append({"enabledPath": enabled_path, "path": str(root),
                            "descriptorSha256": inputs.digest(descriptor), "replacePaths": list(replaces)})
    return roots, {"path": str(config.resolve()), "sha256": inputs.digest(config), "descriptors": descriptors}, installation


def virtual_files(roots: Iterable[SourceRoot], folder: str) -> list[Path]:
    winners: dict[str, tuple[int, str, Path]] = {}
    for priority, source in enumerate(roots):
        for replacement in source.replace_paths:
            if folder.casefold() == replacement.casefold() or folder.casefold().startswith(replacement.casefold() + "/"):
                winners.clear()
            elif replacement.casefold().startswith(folder.casefold() + "/"):
                prefix = replacement[len(folder) + 1:].casefold()
                winners = {key: value for key, value in winners.items() if key != prefix and not key.startswith(prefix + "/")}
        directory = source.root / folder
        if not directory.is_dir():
            continue
        for path in directory.rglob("*.txt"):
            relative = path.relative_to(directory).as_posix()
            winners[relative.casefold()] = (priority, relative, path)
            if len(winners) > MAX_FILES:
                raise CatalogError("Native virtual file inventory exceeds bounds")
    return [value[2] for value in sorted(winners.values(), key=lambda item: (item[0], item[1]))]


def records(roots: list[SourceRoot], folder: str, inputs: Inputs):
    result = {}
    overrides = []
    for path in virtual_files(roots, folder):
        text = inputs.read(path)
        entries = list(Node(text, 0, len(text)).entries())
        variables = {key: value.scalar() for key, value in entries if key and key.startswith("@") and not value.block}
        for key, value in entries:
            if key is None or key.startswith("@") or not value.block:
                continue
            if key in result:
                overrides.append({"key": key, "old": str(result[key][1]), "new": str(path.resolve())})
            result[key] = (value, path.resolve(), inputs.digest(path), variables)
    return result, overrides


def numeric(raw: str | None, variables: dict[str, str]):
    visited = set()
    while raw is not None and raw.startswith("@") and raw in variables:
        if raw in visited or len(visited) >= 16:
            return None
        visited.add(raw)
        raw = variables[raw]
    if raw is None or len(raw) > 64 or not re.fullmatch(r"[+-]?[0-9]+(?:\.[0-9]+)?", raw):
        return None
    try:
        value = float(Decimal(raw))
        return value if math.isfinite(value) else None
    except (InvalidOperation, OverflowError):
        return None


GATE_FIELDS = {"can_recruit", "should_show_when_unavailable", "access_through_subject", "special_recruit_only",
               "allowed_in_hired_troops", "fallback_in_hired_troops_if_unlocked", "holy_order_fallback"}
STAT_FIELDS = {"damage": "damage", "toughness": "toughness", "pursuit": "pursuit", "screen": "screen",
               "stack": "stack", "hiredStackSize": "hired_stack_size", "siegeValue": "siege_value", "siegeTier": "siege_tier"}


def build_catalog(ck3: Path, user: Path):
    user = user.resolve()
    inputs = Inputs()
    roots, mod_list, installation = active_roots(ck3, user, inputs)
    maa, maa_overrides = records(roots, "common/men_at_arms_types", inputs)
    cultures, culture_overrides = records(roots, "common/culture/cultures", inputs)
    pillars, pillar_overrides = records(roots, "common/culture/pillars", inputs)
    era_rows, _ = records(roots, "common/culture/eras", inputs)
    innovation_rows, _ = records(roots, "common/culture/innovations", inputs)
    definitions = []
    for key, (value, path, digest, variables) in maa.items():
        fields, duplicates = scalar_fields(value)
        gates = "\n".join(name + "=" + child.raw() for name, child in value.entries()
                          if name and (name in GATE_FIELDS or "era" in name))
        explicit_fights = fields.get("fights_in_main_phase")
        fights = True if explicit_fights is None else {"yes": True, "no": False}.get(explicit_fights)
        category = fields.get("type")
        for field in ("type", "fights_in_main_phase"):
            if field in duplicates and len(set(duplicates[field])) > 1:
                if field == "type": category = None
                else: fights = None
        stats = {name: numeric(fields.get(native), variables) if len(set(duplicates.get(native, []))) <= 1 else None
                 for name, native in STAT_FIELDS.items()}
        definitions.append({"key": key, "category": category, "fightsInMainPhase": fights,
            "fightsInMainPhaseExplicit": explicit_fights, **stats, "sourceFile": str(path), "sourceSha256": digest,
            "recruitmentOrEraGatesRaw": gates, "conditionalEligibilityUnresolved": True,
            "availabilityInterpretation": "Resolution audit only; do not infer recruitment eligibility from this catalog.",
            "innovationReferences": sorted(set(re.findall(r"\binnovation_[A-Za-z0-9_]+\b", gates))),
            "cultureParameterReferences": sorted(set(re.findall(r"\b(?:unlock_maa|maa_unlock)_[A-Za-z0-9_]+\b", gates))),
            "duplicateScalarFields": duplicates,
            "unresolvedNativeSemantics": category is None or fights is None,
            "unresolvedNumericFields": {name: fields.get(native) for name, native in STAT_FIELDS.items()
                                        if native in fields and stats[name] is None}})
    culture_list = [{"name": key, "heritage": scalar_fields(value)[0].get("heritage"),
                     "sourceFile": str(path), "sourceSha256": digest, "conditionalEligibilityUnresolved": True}
                    for key, (value, path, digest, _) in cultures.items()]
    heritages = [{"name": key, "sourceFile": str(path), "sourceSha256": digest}
                for key, (value, path, digest, _) in pillars.items() if scalar_fields(value)[0].get("type") == "heritage"]
    eras = [{"key": key, "earliestYear": numeric(scalar_fields(value)[0].get("year"), variables),
             "sourceFile": str(path), "sourceSha256": digest, "raw": value.raw(), "yearIsNotARecruitabilityGuarantee": True}
            for key, (value, path, digest, variables) in era_rows.items()]
    era_years = {row["key"]: row["earliestYear"] for row in eras}
    innovations = []
    for key, (value, path, digest, _) in innovation_rows.items():
        era = scalar_fields(value)[0].get("culture_era")
        innovations.append({"key": key, "cultureEra": era, "earliestEraYear": era_years.get(era),
            "directUnlockMaa": [v.scalar() for k, v in value.entries() if k == "unlock_maa" and not v.block],
            "customTooltipKeys": [v.scalar() for k, v in value.entries() if k == "custom" and not v.block],
            "sourceFile": str(path), "sourceSha256": digest, "gatingRaw": value.raw(), "conditionalEligibilityUnresolved": True})
    innovation_lookup = {row["key"]: row for row in innovations}
    for row in definitions:
        def hint(key):
            innovation = innovation_lookup.get(key, {})
            return {"innovation": key, "cultureEra": innovation.get("cultureEra"),
                    "earliestEraYear": innovation.get("earliestEraYear"), "conditionalNotGuaranteed": True}
        row["innovationEraReferenceHints"] = [hint(key) for key in row["innovationReferences"]]
        row["directUnlockInnovations"] = [hint(i["key"]) for i in innovations if row["key"] in i["directUnlockMaa"]]
    info = installation / "game/common/men_at_arms_types/_men_at_arms_types.info"
    semantics = {"fightsInMainPhaseDefault": True, "defaultEvidenceFile": None, "defaultEvidenceSha256": None, "defaultEvidenceLine": None}
    if info.is_file():
        text = inputs.read(info)
        evidence = next((i + 1 for i, line in enumerate(text.splitlines()) if "fights_in_main_phase" in line and "Default yes" in line), None)
        semantics.update(defaultEvidenceFile=str(info), defaultEvidenceSha256=inputs.digest(info), defaultEvidenceLine=evidence)
    catalog = {"schemaVersion": 1, "scope": "Active native CK3 registry for exhaustive mapper resolution, not recruitment eligibility",
        "activeModCount": len(mod_list["descriptors"]), "activeModList": mod_list, "nativeSemantics": semantics,
        "definitions": sorted(definitions, key=lambda row: row["key"]), "cultures": sorted(culture_list, key=lambda row: row["name"]),
        "heritages": sorted(heritages, key=lambda row: row["name"]), "cultureEras": eras, "innovations": innovations,
        "innovationCount": len(innovations), "overrides": {"maa": maa_overrides, "cultures": culture_overrides, "pillars": pillar_overrides},
        "unresolvedConditionalEligibility": True,
        "availabilityWarning": "CK3 culture-era years and mapper-era boundaries are independent. Trigger/innovation references are conditional; event and special troops may exist without normal recruitment. Do not silently filter them from resolution coverage.",
        "counts": {"definitions": len(definitions), "cultures": len(culture_list), "heritages": len(heritages),
                   "categories": dict(Counter(row["category"] or "<unresolved>" for row in definitions)),
                   "noncombatants": sum(row["fightsInMainPhase"] is False for row in definitions)},
        "inputFiles": inputs.files}
    protected = [installation, user] + [source.root for source in roots]
    return catalog, protected


def write_catalog(catalog: dict, output: Path, protected_roots: Iterable[Path]) -> Path:
    output = output.resolve()
    destination = output if output.suffix.casefold() == ".json" else output / "native-catalog.json"
    for root in protected_roots:
        if destination.is_relative_to(root.resolve()):
            raise CatalogError("Output must be outside the CK3 installation, user directory and active mod roots")
    if output.exists() or destination.exists():
        raise CatalogError("Output must be new; existing files/directories are never overwritten")
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation also rejects a concurrent writer after the existence check.
    with destination.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(catalog, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")
    return destination


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ck3", type=Path, required=True, help="CK3 executable under the installation's binaries directory")
    parser.add_argument("--ck3-user-dir", type=Path, required=True, help="User directory containing dlc_load.json and mod descriptors")
    parser.add_argument("--out", type=Path, required=True, help="New external JSON file or new directory")
    args = parser.parse_args(argv)
    try:
        catalog, protected = build_catalog(args.ck3, args.ck3_user_dir)
        destination = write_catalog(catalog, args.out, protected)
        print(json.dumps({"success": True, "output": str(destination), "counts": catalog["counts"],
                          "activeMods": catalog["activeModCount"], "recruitabilityEvaluated": False}))
        return 0
    except (OSError, ValueError, UnicodeError) as error:
        print("Native catalog failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
