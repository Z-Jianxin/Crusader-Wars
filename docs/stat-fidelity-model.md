# CK3 stat fidelity model and current limits

The experimental **Preserve CK3 combat stats** option replaces selected XP approximations with generated Attila database profiles. It does not promise identical battle outcomes or identical strength between different unit types. Tactical deployment, armour interaction, hit probability, formation, morale, fatigue, range and pathfinding remain Attila mechanics.

## Evidence and conversion

[StatExport.cs](../CrusaderWars/stat_fidelity/StatExport.cs) accepts a bounded `CCSTAT|v1` stream. Each snapshot requires matching `BEGIN` and `END` identifiers. Only the latest frame is considered; an incomplete new frame cannot revive an old battle. Limits are 32 MiB of log text, 2,048 characters per record and 20,000 records. Numeric values use invariant decimal syntax. Unknown values, unsupported versions, duplicate identities and invalid numbers generate diagnostics instead of inferred values.

Record layouts:

```text
CCSTAT|v1|BEGIN|snapshot-serial
CCSTAT|v1|REGIMENT|side|armyId|regimentId|ownerId|type|stationProvinceId|damage|toughness|pursuit|screen|baseDamage|baseToughness
CCSTAT|v1|COMMANDER|side|characterId|advantage|damagePerAdvantage
CCSTAT|v1|KNIGHT|side|characterId|prowess|effectivenessMultiplier|damagePerProwess|toughnessPerProwess
CCSTAT|v1|END|snapshot-serial
```

`side` is explicitly `attacker` or `defender`. `source` in the type field means resolve the real type from the exact save regiment identity; localized display names are never identifiers. `none` is permitted for an unavailable army ID when side, regiment ID and owner ID still match the participating save chunk. A verified commanderless side uses character ID `none` and advantage zero. `unknown` is rejected. Exported records from unraised or otherwise nonparticipating regiments are not applied to units.

[SnapshotEvidence.cs](../CrusaderWars/stat_fidelity/SnapshotEvidence.cs) then binds `snapshot-N` to the selected player's saved `ccstat_snapshot_serial` and requires `ccstat_combat` to match the combat selected by the existing save reader. Parsing is bounded to a 2 MiB character block and ignores braces in quoted text. The numeric save encoding was verified in the current engine: an explicitly set value of 1 produces `type=value identity=100000`. Profiles and source-preserving grouping are inactive until binding succeeds. Missing or stale evidence is rejected before conversion in the default strict mode. The accepted combat-scope encoding still needs verification in an actual battle handoff fixture; an unexpected encoding fails safely.

For a matched regiment:

```text
damage multiplier = effective CK3 damage / base CK3 damage
HP multiplier     = effective CK3 toughness / base CK3 toughness
```

The mapped Attila template supplies the reference type. This preserves upgrades relative to that template. It does not by itself establish absolute balance across different CK3 types or different Attila unit mappers. The exported owner/station/title getter's coverage of combat-only counters, terrain and army/accolade bonuses must be established separately; a field named effective is not proof of complete combat coverage.

For commander-derived advantage, both sides and the active rules coefficient must be present and agree:

```text
side damage multiplier = 1 + max(0, own advantage - opposing advantage) * active coefficient
```

This multiplier is applied once to that side's represented troops, including the general's bodyguard. On those profiles, legacy commander and commander-trait XP is removed. The general has zero XP, a constant one-star baseline and a configured minimal bodyguard of two soldiers. Title/culture still select its template; those two engine representatives are not a claim that commander prowess deals CK3 combat damage. Conditions currently converted by the separate legacy modifier system remain XP. No extra Martial coefficient is added on top of exported advantage. The coefficient is supplied by the active game definition, not a historical hardcoded CK3 rule.

Pursuit and screen are retained as source evidence but currently do not modify Attila movement or chase damage. Siege-progress stats are not treated as field weapon damage; siege engines retain legacy mapping. Levies and garrisons can receive the verified commander contribution, but their independent CK3 damage/toughness are not imported by the regiment schema yet. Their coverage entry says mapped base stats.

## Source identities and battle results

[Unit](../CrusaderWars/data/save_file/Armies_Objects.cs) retains army, army-regiment, regiment, chunk and owner IDs separately from the mapped Attila key. Units with different source chunks cannot be combined merely because they share a type and culture. Every Attila split copies the source identity. Generated database keys are used for battle XML only; original keys still identify templates and display names.

[CasualtyProcessor](../CrusaderWars/data/battle_results/CasualtyProcessor.cs) adds the reports for every split belonging to an exact source chunk. It never borrows the first report for another same-type regiment. Missing or ambiguous split reports abort the save-update path. Starting and remaining numbers use the same scale. Survivor counts are bounded by the original source population.

A source chunk rounded to zero by battle scaling cannot be represented faithfully and produces an explicit error asking for a larger battle scale. A one-soldier source unit is emitted instead of silently dropped. Disabled mode retains legacy grouping and behavior.

## Knight calibration

Direct knight conversion additionally requires `EnableKnightCalibration` in `settings/StatFidelity.json`. It is **false by default** because the chosen Attila template has not been calibrated against CK3 absolute output.

The explicit test reference is four bodies per knight at prowess 10 and total effectiveness 1.0, with reference damage/toughness per prowess 50/10. These are calibration settings, not assertions of equivalence. The snapshot supplies actual active coefficients, so a rule or mod changing them affects the computed ratio. Total strength is the sum of each knight's prowess multiplied by its actual effective efficiency and current coefficient. The current representation is one aggregate formation; individual knight identity remains in the source roster, but individual battlefield kills are not measured independently.

With complete evidence and calibration enabled, knight body counts no longer depend on prowess, prowess XP is zero, prominent knights remain in their knight pool instead of also increasing MAA XP, and uncalibrated accolade abilities are not stacked on the aggregate. This applies only when the original complete roster has evidence, before legacy redistribution. If both the save's actual knight roster and the export identify the commander as a fighting knight, that character's budget is captured before removing the duplicate knight object and applied once to the general's formation, adjusted for its configured body count. This avoids two character-fate rolls. The ordinary commander representation has no prowess scaling. General-template versus knight-template absolute calibration is still unverified. A zero-strength knight roster generates no fighting unit. The calibrated formation is not battle-scaled, so its result counts are not incorrectly unscaled. Death and kill reports use its actual unique unit ID.

## Configuration and reporting

[StatFidelitySession.cs](../CrusaderWars/stat_fidelity/StatFidelitySession.cs) reads optional `settings/StatFidelity.json`:

```json
{
  "MinimumMultiplier": 0.01,
  "MaximumMultiplier": 1000,
  "CommanderBodyguardSoldiers": 2,
  "RequireCompleteRegimentCoverage": true,
  "RequireCompleteExport": true,
  "EnableKnightCalibration": false,
  "KnightBodiesPerCharacter": 4,
  "KnightReferenceProwess": 10,
  "KnightReferenceDamagePerProwess": 50,
  "KnightReferenceToughnessPerProwess": 10
}
```

Out-of-bounds values fail explicitly. There is no silent clamp, and a zero value is not raised to a positive floor. The Attila backend separately rejects unsupported table references, invalid field ranges or integer overflow. A failed generation cannot install XML pointing to missing generated profiles. Profile registrations reset for each regeneration attempt, including manual unit replacements.

The generated `data/stat_fidelity_coverage.json` records accepted evidence, per-profile scope, configured limits and diagnostics. Disabled mode preserves the legacy path. When enabled, the default `RequireCompleteExport` rejects absent, incomplete or invalid snapshots and requires both sides' commander evidence; the error explains how to disable the option for legacy conversion. Enabled knight calibration also requires the complete original roster before any redistribution. A completed snapshot missing a participating non-siege MAA record fails conversion by default, preventing an asymmetrically upgraded battle. Failures happen before installing the new pack or updating CK3 saves, and write the coverage report.

Development configuration may explicitly set `RequireCompleteExport` and/or `RequireCompleteRegimentCoverage` false. The applicable unmatched data then retains legacy behavior with diagnostics. Such a configuration is partial coverage and is not a claim of a lossless full-combat translation.

## Verification completed

- [Model tests](../Tests/StatFidelity.Model.Tests/StatExportTests.cs) and [snapshot tests](../Tests/StatFidelity.Model.Tests/SnapshotEvidenceTests.cs): 28 tests passed for framing, stale-frame rejection, bounds, invalid numbers, duplicate identities, active advantage coefficient, knight coefficients, source survival calculations and snapshot/save binding.
- [Integration tests](../Tests/StatFidelity.Integration.Tests/SourceIdentityTests.cs): 17 tests passed against the actual application classes for source-preserving culture grouping, split identity, exact casualty allocation, consistent battle scaling, rejection of missing/invalid split reports, strict opt-in gating, explicit development fallback, generated profiles that preserve template identity, the production log sanitizer, fighting-commander budget preservation across regeneration, absent/duplicate calibrated-knight reports, and rejection of an unmapped source regiment before it can be dropped.
- The application builds with .NET 9: zero errors; inherited compiler/analyzer warnings remain.
- The [offline battle replay harness](../Tests/StatFidelity.BattleHarness/README.md) builds and isolates copies of a real save, its log and runtime assets before calling the production readers/mapper/generation code. `--export-only` validates source coverage without battle-layout generation. The real non-battle probe04 save/log was correctly rejected for missing combat export; this is a negative test, not a successful end-to-end replay.

These are parser, calculation and application integration checks. They do not establish full live CK3 export correctness, Attila battlefield behavior, mapper calibration, or a successful round trip through a personal campaign. The complete snapshot-to-save binding and live export fields must be verified with an actual battle handoff fixture before operational validation is claimed.
