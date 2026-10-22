# Reviewed Earl cavalry adapters

The adapter supplies a private one-rider template for reviewed Earl cavalry graphs whose nominal men and mounts differ. It leaves CK3 stock, active/soft counts, XML cohort size and source casualty accounting to the population ledger. For example, the active High Medieval French chevalier template declares 70 men and 55 mounts; its generated private land record declares 70 mounts. It does not create another 15 CK3 soldiers.

## Eligibility and preserved values

The source-controlled [catalog](../CrusaderWars/packfile/stat_fidelity/CavalryBodyAdapter.Catalog.cs) binds the unit's structural database graph, including rider/mount entities, their types, animation references, visual variants, category, class, armour key and missile role. [Runtime asset evidence](../CrusaderWars/packfile/stat_fidelity/CavalryAssetEvidence.cs) additionally resolves the active pack order and checks the winning horse variant-definition bytes and rider/mount animation-block hashes. A later pack replacing the same asset key is therefore checked; conflicting animation definitions are rejected conservatively.

The review requires a man skeleton linked to the matching horse animation table, a horse skeleton, and a concrete horse body mesh in the mount variant definition. Earl's `giant` entity label is accepted only for a catalogued horse graph meeting those checks. The adapter never changes that label, mass, speed, collision shape, animation, armour, or visuals. Camel, elephant, chariot, engine and unknown graphs receive no guessed adapter. Mounted infantry with pack horses is not treated as horse archers.

Numeric combat overrides remain authoritative. Counts must satisfy `0 < mounts <= men`; private mounts are then set to the current nominal men. Damage, rider HP, bonus HP and horse HP come from the effective active records. Neutral profiles preserve the HP components exactly. Scaled profiles use the existing proportional component algorithm, rounding total mounted HP and dismounted rider-plus-bonus HP consistently. Query and generation use the same adapter resolver. Queries retain raw counts and entity labels alongside planned counts and provenance.

## Mapping scope

The [regional role catalog](../CrusaderWars/unit_mapper/CavalryRoleCatalog.cs) supplies explicit High/Late/Renaissance French, English and Sicilian heavy choices, English/Sicilian light choices in all three eras, and French light cavalry in High Medieval only. High Frankish Conrois can use `mk_fre_t1_chevaliers_mounted`, rather than the old Roman horse-archer fallback. Generic German candidates with missing visual variants and later French light candidates with mismatched animation pairs are excluded; any eligible authored regional alternatives still use the normal resolver. Early Medieval and custom mapper folders are unaffected. Title, manual, autofix and edited/custom mapper choices retain their existing protection. A valid existing Earl choice is retained. Horse-archer candidates still require a sustained missile role; regional crossbowmen and mounted infantry are not automatically substituted for CK3 horse archers. The known heavy `cataphract_archers` hybrid keeps its ranged requirement.

The installed later-era stack reviewed on 2026-10-22 matches 1,527 catalog graphs, including 110 horse graphs labelled `giant`: 677 heavy, 488 light, 273 mounted missile, 83 heavy mounted missile and 6 without an assigned CK3 role. These are graph classifications, not claims about exact armour quality, historical recruitment, or absolute balance. Unsupported rows remain explicit.

## Final four-era mapping audit

The actual resolver completed **45,288 queries**, covering all 111 native types across 58/147/102/101 reachable faction/default contexts. All four eras had zero unexpected resolver exceptions and zero resolution errors. The [portable receipt](cavalry-adapters-validation.json) binds the source and assembly hashes, commands, query counts, raw/planned examples and unsupported classifications. Static culture coverage does not establish recruitment eligibility, and accepted adapter mappings still require engine confirmation.

| Era | Queries | Accepted combatant mappings | Explicitly unsupported | Noncombatant stock mappings | Previously unsupported |
| --- | ---: | ---: | ---: | ---: | ---: |
| Early | 6,438 | 5,184 | 732 | 522 | 726 |
| High | 16,317 | 12,220 | 2,774 | 1,323 | 4,210 |
| Late | 11,322 | 8,881 | 1,523 | 918 | 2,787 |
| Renaissance | 11,211 | 8,709 | 1,593 | 909 | 2,370 |

| Era | Heavy cavalry accepted / queried (previous accepted) | Light cavalry accepted / queried | Horse archers accepted / queried | Conrois accepted / queried |
| --- | ---: | ---: | ---: | ---: |
| Early | 551 / 696 (553) | 750 / 754 | 460 / 464 | 45 / 58 |
| High | 1,556 / 1,764 (23) | 1,582 / 1,911 | 1,089 / 1,176 | 147 / 147 |
| Late | 1,192 / 1,224 (0) | 1,171 / 1,326 | 760 / 816 | 102 / 102 |
| Renaissance | 1,055 / 1,212 (275) | 1,191 / 1,313 | 782 / 808 | 98 / 101 |

The stricter graph checks also reject some mappings that previously used a substitute: Early loses six cavalry queries; High loses 227 light and 32 horse-archer queries; Late loses 88 light and 15 horse-archer queries; Renaissance loses 25 heavy, 122 light and three horse-archer queries. These conservative reductions are recorded explicitly. Unknown or changed cavalry graphs are not silently replaced to improve a coverage total. A separate regression ensures that a native **foot** unit incorrectly mapped to an unsupported horse can still receive its verified foot-role repair.

Norman Conrois resolves to `mk_fre_t1_chevaliers_mounted` in High (planned HP 219), `mk_fre_t2_chevaliers_mounted` in Late (255), and `mk_sic_t3_condo_maa` in Renaissance (275). Each source declares 70 men/55 mounts and generates a private 70/70 template. These are planned composite HP values. Norman Late light horsemen remain unsupported because the source animation pair is unreviewed. High/Late Norman horse archers still use the eligible authored Roman fallback, while Renaissance uses an authored mounted crossbow template; full cultural and weapon identity is not claimed.

The remaining unsupported categories include native families without a population contract, combat artillery without an explicit engine-count contract, unreviewed cavalry graphs, missing visuals/references, and absent compatible authored roles. Global catalog size therefore does not imply that every culture, era or role has a supported default mapping.

## Evidence and remaining limit

- Backend suite: 50 passing tests; two pre-existing optional installed-pack tests skipped. Focused cases cover raw/planned counts, source immutability, neutral/scaled components, numeric overrides, same-key asset replacement and conflicting animation IDs.
- Native catalog tooling: seven Python tests passed; the cavalry catalog builder passed Python AST parsing.
- The actual full later-era stack generated 14 variants: neutral and scaled versions of seven heavy/light/missile and ordinary/`giant` horse templates.
- RPFM encoded and decoded all 158 rows across 10 generated tables without a cell difference. Source metadata snapshots remained unchanged.
- External evidence: `conflicts-20261021/cavalry-adapter-20261022/`, including `catalog-evidence.json`, `effective-units.json`, `generated/`, and `pack-roundtrip/receipt.json`.

**Engine confirmation is pending for these adapters.** Ordinary vanilla horse rider-plus-bonus-plus-mount HP has prior native card/combat evidence; the new Earl normalization and `giant` horse variants have offline graph, field and binary evidence only. Every adapter reports `EngineValidated=false`. No game/UI test was run for this change. A future manual trial should check actual rider/mount counts, selected-unit HP where visible, dismounting, movement/collision and combat for ordinary and `giant` representatives before claiming engine equivalence.

The [catalog refresh tool](../Tests/AttilaCalibration/build_cavalry_catalog.py) operates only on an explicitly supplied effective DB and asset snapshot. A catalog refresh requires review; newly appearing keys or asset hashes do not become trusted automatically at runtime. Its role labels describe reviewed armour/class/missile data and do not reuse Earl's training labels as weight classes.
