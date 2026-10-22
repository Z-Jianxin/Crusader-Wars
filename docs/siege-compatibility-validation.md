# Siege mapping and result-accounting validation

**Superseded for field-battle participation:** the later native audit established that this fixture's onagers and mangonels have explicit combat `current=0` while their source equipment remains intact. Their CK3 definitions also set `fights_in_main_phase = no`. The machinery/counting and synthetic serialization checks below remain historical implementation evidence, but emitting 60 onagers and 10 mangonels in this field battle was not a faithful participation policy. The [mapping and commander validation](./mapping-and-commanders-validation.md) records the subsequent correction and its final release status.

Global equipment stock, active field participants and historical combat starting counts are distinct. Reserved machines must remain in source/army-regiment stock, contribute no emitted field formation, and stay out of combat-local current and `total_fighting_men`. Native result initial counts include equipment, so reserve stock must not be converted into artificial losses when computing result statistics. The pending native `final_count` accumulator remains untouched. No new native completion test of reserved equipment is claimed here.

This investigation follows a disposable user battle exported as `snapshot-4` on 1066-11-11. The actual Attila battle finished with **Victory**, but the companion rejected its result while processing `mangonel_160`. The observed battle is failure evidence, not a successful campaign return. No UI test, game launch, result-save recovery or native reload was performed for this investigation.

Raw saves, logs, launched packs and machine-specific paths remain outside Git. The immutable input save SHA-256 is `665AF1FFE93015173C174E09B346928FC9483D190318360B3207FFC042EA4D27`; the raw AAR SHA-256 is `B88D546986ACC9F4CAB35C7A1046046929707607F98132CF4126074C509C013F`; the actually launched pack SHA-256 is `33234083C1C2282EEAF2CB60D8D9612E875994F75E104CBFC1E7A9726995C3BC`.

## Generation and import defects

The twenty three-person formations were ordinary infantry proxies for onagers. Their actual mapped key, `SUEB_skiltfolk_sword`, has no artillery engine and zero guns in the selected database. The old generator treated the mapper's siege flag as sufficient, inferred one machine per formation, and converted each CK3 machine to three soldiers. These were not legitimate artillery crews or knight retinues.

The saved attacker has three distinct event onager sources, each with 20 machines: sources `15155`, `15158` and `15161`, owned by character `33624`. The first source produced twenty infantry proxies, totalling 60 bodies. The per-side cap then omitted the other two sources. All twenty emitted proxies survived at 3/3 in both actual result phases. Correct handling must distinguish represented machines, deployed bodies and omitted source quantities; it must not treat the cap as evidence that omitted machines died.

The defender's separate mangonel source `17625`, army regiment `84`, contains ten CK3 machines. It mapped to the actual artillery template `bel_ost_cheiroballista` and emitted three formations with XML body counts **14, 14 and 7**. The first result phase recorded **14, 14 and 4** survivors; the second recorded **14, 14 and 3**. Those counts are valid against the launched XML. The later casualty-report step compared incompatible machine/body quantities and raised the impossible-survivors error. This is a converter accounting defect, not an incomplete or inflated AAR.

The relevant boundaries are [unit generation](../CrusaderWars/twbattle/UnitsFile.cs), [deployed-roster binding](../CrusaderWars/stat_fidelity/StatFidelitySession.cs), [raw report validation](../CrusaderWars/stat_fidelity/BattleReportEvidence.cs) and [casualty processing](../CrusaderWars/data/battle_results/CasualtyProcessor.cs).

The first repaired-generation replay exposed a third, older reader defect during synthetic serialization. The native hired levy source `13736:0` and raised army-regiment `73` both have current **456**, maximum **477**; hired bow source `13737:0` and army-regiment `74` both have current **343**, maximum **358**. Their historical combat starting counts are 477 and 358. The legacy reader restored those starting counts before generation, undoing casualties that occurred before the handoff. The strict source editor correctly refused the resulting increase; its bounds were not loosened. Historical starting count happens to equal maximum in these two cases, but those are distinct fields.

The [bound-fidelity reader](../CrusaderWars/data/save_file/ArmiesReader.cs) now preserves the native per-chunk current counts and leaves historical starting fields unchanged. The disabled/unbound legacy path retains its prior behavior. [Focused regressions](../Tests/StatFidelity.Integration.Tests/NativeSourceCurrentTests.cs) cover the real hired-source shapes, unequal losses in multiple chunks, preserved history, unchanged-source serialization, rejection of healing, and legacy starting values below maximum. Test execution status belongs in the final subsection below.

## Mapper and mod provenance

The captured CK3 configuration has 46 enabled entries, including the fidelity exporter immediately before the compatibility patch, and no disposable debug probe. Its native snapshot serial, selected combat, province and source identities match the saved battle. No enabled CK3 mod in this configuration supplies a `common/men_at_arms_types` override; effective owner modifiers are still relevant and are exported by the engine.

The selected Attila era is Early Medieval 919. Its launch list contains the 919 packs and compatibility fix, Age of Justinian dependencies, the Mubarizun pack and both Dahan packs. Later-era Earl packs are absent, as expected for 1066. The optional-mod selection file is empty; Dahan is enabled through the mapper's submod selection.

The captured mapping trace first resolves Norman/Frankish to `Latin`, then the enabled Dahan culture overlay changes it to `Imazigen`. The official Early faction file supplies the Imazigen onager-to-infantry mapping. This did not originate in a manual or autofix replacement. The same Dahan culture file and hash exist in the untouched original companion assets, so it is inherited mapping data rather than a recent conversion-code mutation.

This overlay is broad: excluding the synthetic `Default` entry, it contains 328 distinct culture names and 399 heritage/culture pairs. Compared with the base Early culture file, 192 shared pairs differ, affecting 149 distinct cultures. The Norman change is one of them. These differences do not, by themselves, establish that every override is erroneous. The investigation does not justify a blanket non-Asian filter or a global culture rewrite. A compatibility correction must be limited to verified invalid siege representations and must preserve explicit user replacements and unrelated author mappings.

## Large stats are already present in CK3

The saved campaign enables `happy_difficulty`. The active local `divinity` game rule applies a player modifier with +150 skills, while its skill defines cap Martial and Prowess at 100. The native handoff consequently reports the player commander's Martial 100 and advantage **106**, versus the opposing commander's advantage **26**. The active exported advantage coefficient is **0.05**. The supported commander conversion therefore produces `1 + (106 - 26) × 0.05 = 5` damage on the attacker side.

Owner toughness bonuses are also native getter outputs. The attacker levy is **10 damage / 31.32 toughness**, against base **10 / 10**. Attacker MAA toughness ratios range from **2.71 to 3.21** in this snapshot; the defender's ordinary owner ratio is **1.06**. These values should not be normalized away merely because the active mod list makes them large. The precise contribution of every building, perk and modifier was not independently decomposed here.

The following actual launched database rows were checked against their active base templates and the exported ratios. Integer results use the backend's rounding. This checks application of the captured values; it does not claim that nominal weapon damage equals measured DPS.

| Attacker profile | Damage factor, including commander once | Base → launched melee damage + AP | Toughness factor | Base → launched HP |
|---|---:|---:|---:|---:|
| Levy, `ALL_garrison_archers` | 1 × 5 | 8 + 4 → 40 + 20 | 3.132 | 100 → 313 |
| Light footmen, `mubs_maghrebi_light_footmen` | 1.2 × 5 | 18 + 0 → 108 + 0 | 2.81 | 114 → 320 |
| Pikemen, `mubs_maghrebi_pikemen` | 1 × 5 | 17 + 0 → 85 + 0 | 3.21 | 125 → 401 |
| Conrois, `ARM_light_cavalry` | 1.2 × 5 | 6 + 4 → 36 + 24 | 3.11 | Rider 100 → 311; mount 110 → 342 |
| Bowmen, `ALL_garrison_archers` | 1.3 × 5 | 8 + 4 → 52 + 26 | 2.71 | 100 → 271 |

The defender huscarl retains base melee damage **46 + 4** and scales HP **100 → 106**; the mercenary bowmen retain **7 + 2** and scale HP **100 → 99**. These sampled launched rows show neither a second owner multiplier nor a second commander multiplier. [Profile application](../CrusaderWars/stat_fidelity/StatFidelitySession.cs) and [database generation](../CrusaderWars/packfile/stat_fidelity/StatFidelityBackend.cs) remain the relevant implementation boundaries.

## Result evidence and limits

Both actual AAR phases contain exactly all **53** launched formation identities, with one survivor row and one kill row per formation. There are no duplicates, omissions, unknown identities, counts above the XML deployment, or survivor increases between phases. Raw survivor totals are attacker **5,338 → 5,328**, defender **872 → 701**. These are Attila bodies, not directly interchangeable CK3 troop totals.

The failed processing attempt reached casualty-report construction but never reached the character-fate stage. Its captured debug log contains neither the start nor completion fate marker, and no fate ledger was captured. The commander's native fallen marker alone does not establish death or capture. The user designated this battle disposable, so no recovery or new fate roll was performed.

Offline checks cannot establish repaired engine gun/crew spawning or a successful campaign return. Any generated artillery variant must account for the database's actual gun count as well as XML crew counts. Compatibility checks must preserve mod-provided effective stats and explicit mapping overrides while rejecting genuinely unsupported representations.

## Final checks and deployment

The repaired path is enabled for a complete, save-bound CK3 combat-stat export. It queries the actual loaded artillery records, uses native-size batteries and generates private partial-battery records with matching crew and gun counts. The reviewed Early919/Imazigen/onager correction reuses existing authored artillery mappings; it preserves an already valid active-pack artillery definition and does not override manual/autofix selections. Unsupported representations stop conversion before pack publication. The silent twenty-formation side cap is bypassed, and the complete deployed roster must remain within the existing 4,096-formation report limit.

Each formation records native source machines, deployed crew bodies and scaled Attila engines independently. Native equipment retention uses crew survival weighted by that exact source's represented machines, with rounding once per source. Unrepresented equipment remains unchanged. Crew counts bypass generic battlefield-scale normalization. Pursuit losses are calculated per source before grouping; original source/current bounds and historical combat starting counters remain protected.

Final offline checks passed:

- **175 integration tests**, including current-strength preservation, separate siege sources, partial batteries, 50% scale, cultureless source identity, missing/invalid evidence, pursuit rounding and protected mapper overrides.
- **85 model tests**, **25 backend tests**, and **8 analyzer tests**. Two optional installed-pack backend cases were skipped in this run; the separate active-stack binary roundtrip below was performed.
- Exact captured snapshot-4 input through complete generation, active-stack database generation and synthetic casualty serialization/re-read at **100% and 50%**. All **11 participating MAA source identities**, including all four siege sources, matched their independent expected returned counts. Missing, duplicate and malformed synthetic reports were rejected without source mutation. Input gamestate and achievement eligibility were preserved.
- Independent source/XML/database audits: 100% emits **48 formations / 24 profiles**; 50% emits **36 formations / 27 profiles**. All three twenty-machine onager sources remain distinct. They represent 60 native machines with 60 engines/600 crew at 100%, or 30 engines/300 crew at 50%. Ten native mangonels use 4/4/2 engines at 100%, or 4/1 at 50%, with native source budgets preserved in both cases.
- Offline RPFM encode/decode: **12 tables / 410 rows** matched cell-for-cell, including the partial battery's 20 crew / 2 guns. Seven linked native records and 156 unchanged fields also matched. These generated database tables remained byte-identical after the current-strength reader correction.

An earlier diagnostic replay stopped at the source-current guard before the historical-starting reconstruction was corrected. That failed attempt is retained separately; neither it nor the old user AAR is counted as a successful repaired battle. The final casualty counts above are deliberately synthetic, not a recovered user result.

The self-contained Windows companion was installed separately on 2026-10-22. Assembly SHA-256: `47628230B3A73A458F559AF6409E82A9F11CEB6A9E2A6BBF2380E86D28028016`. All 402 published files were hash-checked. Fifty runtime settings/input/state files were verified unchanged before the disposable interrupted-battle flag alone was archived and cleared. The application was not relaunched; personal CK3 saves and the original converter installation were not modified. The next trial should start a fresh battle.

No UI test, game launch, native engine observation, recovered result save, new character-fate decision or CK3 completion test was performed. The new partial batteries still need user gameplay verification; the offline evidence does not certify all mod lists or reproduce CK3 siege-progress, counter, terrain or pursuit mechanics.
