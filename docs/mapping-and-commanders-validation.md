# Field participation, Norman cavalry and funded commander guards

This update follows the user's screenshots of excessive scorpios, two-person generals and missing recognizable Norman heavy cavalry. It supersedes the field-composition conclusions of the [previous siege receipt](./siege-compatibility-validation.md). That release verified its generated counts and casualty roundtrip but generated the wrong participating army. No UI test or game launch was performed for this update.

## Equipment stock is not field participation

Native snapshot-4 and the newer named snapshot-6 save agree: attacker onager sources 15155/15158/15161 retain 20 machines each, and defender mangonel17625 retains 10. Every corresponding selected-combat record explicitly has current 0 and soft_casualties 0. Their native definitions declare `type=siege_weapon`, `fights_in_main_phase=no` and damage 0. Native `total_fighting_men` excludes this equipment; it is not a continuously updated sum of fractional live combat-current values in the observed save.

The converter now reads native MAA definitions and exact selected-combat participation before applying mapped-unit stats. This is independent of the mapper's siege flag: the official Latin onager mapping is an ordinary infantry proxy without that flag. Neither an infantry proxy nor real artillery should appear for this zero-participation stock. The native definition resolver respects ordered active descriptors, virtual-file replacement and `replace_path` masks.

All 70 machines remain in their source records, with zero Attila onager/mangonel formations. Combat-local current and fighting totals exclude the recorded reserve during return. Native result-statistics survivor targets retain the stock so an initial equipment count does not become a fabricated casualty; pending native `final_count` accumulators remain zero. Positive whole-machine participation can represent only the active portion, with the remainder reserved. Missing/ambiguous evidence and unsupported fractional equipment participation fail explicitly.

## Restore the actual cavalry role

The installed native Conrois definition is heavy cavalry. Both saved sources 15154 and15160 exist; their stock is 100 each and their fractional combat current is 99.68119, explaining the screenshot's99/100 display. They are two distinct regiments, not an accidental split of one regiment.

Two inherited overlay layers obscured them. Dahan's culture file changed Norman/Frankish from Latin to Imazigen, and unrelated faction compatibility files replaced the Latin Conrois template with `ARM_light_cavalry`. That asset actually uses light cavalry equipment. The official DefaultCK3 template, `cha_nor_chef`, is verified in the active database as very-heavy, heavy-mail, kite-shield cavalry with charge 70 and no missile weapon.

A shared in-memory scope policy now covers 32 fingerprinted packaged culture/faction overlays across all four DefaultCK3 eras. Known regional overlays remain authoritative in their Asian domain and additive outside it; unrelated MoreBookmarks compatibility definitions are additive under DefaultCK3. New cultures/types and Dahan's Han/Asian mappings remain available. General, knight, levy, MAA, limit and available-unit caches all read the same scoped document. Edited/custom revisions, title-specific choices and manual/autofix replacements retain their precedence. No mapper XML or Workshop pack is rewritten.

Private profile localization now follows the actual generated role and CK3 type. A guard and MAA sharing one base asset receive separate labels, such as `Commander's guard (Conrois)` and `MAA Conrois`. Literal English CK3 names follow active-mod precedence, including nested localization files; unresolved scripted names fall back to the readable type key. This does not establish Chinese-font rendering or implement the future named-knight plan.

## A guard funded by existing troops

The default commander guard target is 30 deployed bodies. After actual ordinary formations are planned, it detaches up to that many bodies from an eligible same-army cohort, preferring appropriate melee MAA and otherwise verified actual levy troops. It inherits that cohort's template and effective quality. The original formation loses exactly the borrowed bodies; a fully transferred small formation is removed cleanly. There is no free elite unit or extra ordinary manpower.

Martial-derived commander advantage remains applied to the army once. Personal prowess and knight efficiency add one personal combat budget only when the commander is actually a contributing native knight with positive selected-combat participation; the commander is excluded from the pooled knight budget. Its native knight cache/report identity is preserved. The two guards in the real replay have no such personal contribution, matching their native roster evidence.

Guard casualties return through the complete original donor cohort, with explicit scale/body weights and rounding once per source. Kill and pursuit attribution use the same funding identity. General-death tracking still identifies the real commander. Regeneration restores donations before planning again. With no eligible donor or personal budget, the minimum two-body engine placeholder remains; it is not expanded into invented troops.

Larger ordinary CK3 regiments already fill one formation up to configured capacity before splitting. The observed400-man foot/pike/bow cohorts each remained one formation. No giant artificial siege batteries or cross-source cavalry merge was needed.

## Evidence and limits

Installed Steam builds remain CK3 26046173 and Attila25370302. The full offline replay authority is the original snapshot-4 save (SHA-256 `665AF1FFE93015173C174E09B346928FC9483D190318360B3207FFC042EA4D27`) and its matching framed handoff. The newer named snapshot-6 save (SHA-256 `63E19262BD90FB6B8E7E3AD58F987953DD7888869DFF1E7522271ACA9B66BD05`) independently corroborates participation and cavalry identities. Its complete handoff was no longer available; no replacement log was fabricated.

- **228 application integration tests passed**, including native definitions/participation, scoped overlays and caches, cohort sizing, private labels, guard funding and regeneration, source loss, pursuit and fighting-commander identity.
- **85 model tests and 25 backend tests passed**; two optional installed-pack backend cases were skipped. A separate current active-stack binary roundtrip was performed.
- Exact native-input replays passed at 100% and 50% scale, including synthetic casualty serialization and re-read of all 11 MAA source identities. They emitted 30 and 25 formations respectively, with 22 actual stat profiles each. Both scales emit zero artillery and preserve all 70 machines.
- Independent audits verified both Conrois sources and their heavy cavalry records. At100%, one source is 70 ordinary bodies plus 30 borrowed guard bodies, and the other remains 100. At50%, the first is 20 ordinary plus 30 guard, and the other 50. The other funded guard comes from Huscarls. Both guards are 30 bodies and add no unsupported personal budget. Independent generated weapon/entity/mount checks match their donor profiles.
- A current offline RPFM encode/decode preserved all 10 generated tables and 175 rows cell-for-cell, including the distinct private guard/MAA labels and general XML designation. No general-only restriction field was found in the active main/land schemas; this is not native engine validation of the new guard.

An initial diagnostic replay exposed the flag-false infantry proxy bypassing participation checks. The native-definition resolver corrected that failure; the successful outputs are separate. Raw captures, source observations, synthetic outputs and audit receipts remain outside Git. No recovered user result, character-fate decision or personal campaign save was produced.

The self-contained release was installed separately on2026-10-22; assembly SHA-256 is `C1C04D3BA96882ADA7409EA4E1323D0DE5454DCC72C605CB55A55362F314026D`. All402 published files were hash-checked,45 existing runtime state/input/settings files were verified unchanged, and captured mapper XML/settings remained byte-identical. There was no saved guard-size override, so the compiled30-body default applies without rewriting preferences. Neither game nor the application was launched. Detailed deployment verification stays with the external evidence. Start a fresh generated battle after updating. The new guard/appearance still requires the user's gameplay trial; counters, terrain, pursuit mechanics and absolute cross-game combat calibration retain their previously documented limits.
