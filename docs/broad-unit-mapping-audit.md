# Broader unit-mapping audit — 2026-10-22

The earlier updates followed reported failures and shared paths; they did not constitute an exhaustive mapping audit. This pass inventories native definitions, culture inheritance, mapper defaults and overlays, effective installed assets, formation/source accounting and casualty return. No UI test, game launch, recovered campaign result or character-fate decision was performed.

This is the historical baseline. The subsequent [active-population update](./active-population-validation.md) and [reviewed Earl cavalry adapters](./cavalry-adapters-validation.md) address the two major limits identified below. Their receipts distinguish new offline checks from earlier native gameplay evidence.

## Scope and reproducibility

The active 46-mod CK3 configuration contains 111 native MAA definitions, 244 static cultures and 59 heritages. Native definitions, ordered enabled descriptors, file replacements and `replace_path` masks are read from disk. Every culture resolves to a heritage. The catalog also retains conditional recruitment/innovation/era gates; it does not evaluate them or assume every culture recruits every type.

The audit covers all four DefaultCK3 mapper eras with Dahan China selected, and Earl's infantry, missile and cavalry packs in the three later eras. The Early stack selects 10 packs; the later eras share the same 17 physical packs. The audit prepares these databases explicitly without replacing the live Attila load list. Manual extras, other playthroughs and arbitrary custom mappings are not certified.

Each era checks 305 culture contexts: 244 static cultures, 59 heritage-only dynamic-culture fallbacks, explicit Default and a fully unknown fallback. It then exercises the actual resolver for each native type through the factions reached by those contexts. All authored faction/title/role rows and referenced assets are separately inventoried. This includes authored factions not reached by those culture contexts, without claiming their entire resolution cross-product was exercised. Title rows preserve their title keys; their inventory is not every possible title-owner test.

The reusable [audit tool and commands](../Tests/UnitMapping.Audit/README.md) record source/assembly hashes, selected packs, actual main/land/entity/mount/weapon/projectile metadata, resolution routes and diagnostics. A genuine matching native handoff enables the production save-bound policy; synthetic type/culture queries are explicitly labelled. The supplied native living section was verified as an unchanged substring of the original full native save. No handoff or gameplay evidence was fabricated.

The completed baseline contains 44,955 potential type/faction resolution queries and 551,272 selected overlay-filtered author row instances across the eras. These are coverage denominators, not unique soldiers, recruitable combinations or observed failed battles.

| Era | Reachable factions | Native types | Resolver queries | Author row instances |
| --- | ---: | ---: | ---: | ---: |
| Early | 57 | 111 | 6,327 | 102,631 |
| High | 147 | 111 | 16,317 | 182,018 |
| Late | 101 | 111 | 11,211 | 166,410 |
| Renaissance | 100 | 111 | 11,100 | 100,213 |

## Confirmed systemic findings and fixes

**Fallback defaults and troop roles.** Missing native type entries were not merely isolated omissions. The old fallback retained only the last Default block, inferred categories from mapper limits and ultimately selected an alphabetically first key. Order Knights and Teulu therefore became infantry across every tested faction. Partial overlays could erase unrelated earlier defaults. Several inherited authored mappings also crossed foot/mounted or sustained-ranged roles. Selection must use native roles and the effective loaded asset, while preserving explicit title, custom and manual authority.

The corrected automatic policy applies to fingerprinted packaged mappings in a save-bound DefaultCK3 battle. It retains coarse compatible assets or selects an existing same-native-family candidate from that faction, then Default; it does not search the entire database for an arbitrary replacement. Heavy melee cavalry requires a heavy horse template, while the verified Cataphract Archers hybrid retains a sustained-missile requirement. Earl bow infantry is recognized by actual missile/precursor/ammunition fields even when its category says `inf_melee`. Foot melee weapon identity, polearms and armour remain separate review questions; coarse compatibility is not full role fidelity.

Generation refreshes these mappings after the effective database is prepared, because initial army reading happens earlier. Formation limits then use native categories when no explicit custom/numeric limit applies; a successful unknown-type key cannot retain a zero limit and lose its soldiers. Partial Default blocks preserve unrelated earlier types and role pools. Explicit title/manual/autofix/custom choices retain authority, including an override chosen after an automatic repair. File-list/stamp/context changes invalidate cached candidate decisions. Unsupported population/HP models stop explicitly, so a second generic fallback cannot hide them. An explicit appearance choice also cannot turn ordinary native people into siege-machine stock without a representation contract.

**Scaled source accounting.** Ordinary levy survivors and their starting denominator used inconsistent battle-scale conversion. Losses could disappear at reduced scale. Per-formation rounding also changed MAA survivors solely because the same source was split differently. Native 101 men represented by 76 bodies at 75% scale now return 51 native survivors for 38 living bodies, whether one formation reports 38 or two report 19 each. Levy, MAA and funded guards share bounded source-level projection, rounding once per source and applying native pursuit once. Original current/capacity/history bounds remain distinct.

Initial source-strength proofs reject partially missing same-culture levy chunks as well as entirely missing cohorts. Regeneration retains source/count/scale proofs and cannot quietly accept a missing formation as a smaller budget. Native noncombatant equipment is excluded from ordinary MAA accounting before mapper flags are interpreted. Zero-stock knight records receive no prowess budget or fate roll and do not block an active commander's otherwise complete evidence.

**Culture and source coverage.** Small levy cultures could be redistributed into another culture, and same-faction grouping could merge distinct native cultures. Fidelity preserves native culture identity. Positive source chunks and missing levy pools are checked before generation. A source with no culture can use its saved army owner's culture for appearance with a diagnostic; if neither is available it fails explicitly. Its soldiers are never moved into an unrelated unit. Unsupported independent garrison profiles remain explicit.

**Earl pack import.** The actual infantry pack contains a literal lone carriage return inside an unquoted animation string. RPFM exports records with LF; interpreting that embedded character as another row aborted all later-era database preparation. The reader now preserves the field verbatim and recognizes LF/CRLF record boundaries. Regression tests also ensure truncated rows still fail. The third-party data itself is not rewritten or silently trimmed.

**Quoted names.** All 111 Late-era queries for the reachable `Rus' Rebels` faction initially threw an XPath exception before semantic policy ran. Eleven dynamic attribute queries now use one XPath literal encoder. Apostrophes, double quotes, both together and empty values are tested through faction/type/heritage/culture/title and levy/garrison lookup paths. Resolver exceptions are classified separately from intended unsupported-profile rejections.

## Material remaining limits

**Ordinary soft casualties are still represented from source stock.** The newer real native save separates active combat current from knocked-out `soft_casualties`; source stock closely follows their rounded sum. For example, a mercenary levy source holds 456 while selected combat records about 382.85 active and 72.96 soft casualties. The current representation deploys stock, so those unavailable troops can fight again in Attila. The earlier noncombatant equipment reserve fix does not solve this. Correcting it requires a separate active/reserve/recovery ledger and native win/loss/pursuit/all-soft controls, rather than an asset substitution or guessed recovery rule. Those mechanics were not implemented under this audit or presented as verified.

**Later-era body models need further evidence.** Many Earl cavalry records have different main-unit man counts and land-unit mount counts; others use special or multi-rider entities. The current verified ordinary mounted HP formula does not establish those models. A correct visual role is not permission to invent its HP formula or substitute an unrelated vanilla unit solely to pass validation. Unsupported models remain diagnostics pending targeted evidence. Visual-reference existence is also not proof of correct native rendering.

This materially affects playability: the High-era Conrois resolution is unsupported in 145 of the 147 tested faction contexts, including the ordinary mounted-model/candidate limitations. The separate 1066 Norman replay uses the verified Early-era heavy cavalry and passes. The later-era stat build is therefore not broadly ready for cavalry armies. Camel, elephant, gunpowder, nomadic-horde and combat-artillery populations also retain explicit unsupported contracts. All-author inventories still contain missing assets and candidate-role concerns; preserving title/custom authority does not repair a missing optional asset.

**Relative stat scaling is not absolute CK3 balance.** Effective/base damage and toughness preserve upgrades relative to an Attila template. They do not establish equal absolute strength across different native types or different unit packs. Melee/ranged hybrids require contextual review; the native heavy-cavalry family itself includes Cataphract Archers. Equipment-weight and weapon-family findings therefore remain distinct from proven foot/mount mismatches.

Small levy cohorts also retain the inherited weighted random choice of an authored template. Their source population and casualty bounds are now checked, but repeated generation can choose different appearances/reference templates. Deterministic levy selection and common cross-template combat calibration remain further work. Pike weapon identity and heavy-infantry armour are review items rather than consequences of merely passing a foot-versus-mount test.

Counters, terrain/combat width, conditional combat effects, pursuit/screen translation and automatic CK3 loading retain their [draft plans](./combat-mechanics-fidelity-plan.md). [Automatic return](./automatic-battle-return-plan.md) and [named knight retinues](./named-knight-retinues-plan.md) were not implemented here. The current user prohibition on UI testing remains in force.

## Validation record

The final matrix covers 45,288 queries, including the previously omitted explicit fallback faction contexts. Each era has zero unexpected resolver exceptions and zero coarse-role errors among accepted query results. Unsupported cases still stop conversion; completing their audit does not certify their gameplay.

| Era | Queries | Retained | Repaired | Unsupported | Field noncombatants | Missing authored role/title rows |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Early | 6,438 | 3,403 | 1,787 | 726 | 522 | 0 |
| High | 16,317 | 5,925 | 4,859 | 4,210 | 1,323 | 64 |
| Late | 11,322 | 3,778 | 3,839 | 2,787 | 918 | 49 |
| Renaissance | 11,211 | 4,387 | 3,545 | 2,370 | 909 | 46 |

These are potential type/faction combinations, not recruitable units or observed failed battles. Statuses describe the final resolver decision; a retained compatible key may already have been selected by the corrected generic fallback. Authored-candidate missing rows are recorded separately from exercised MAA resolution errors. Foot weapon/hybrid, pike and authored knight concerns remain review findings.

- Final full application integration: **282 passed**. Model: **85 passed**. Backend: **34 passed**, two optional installed-pack cases skipped. Native catalog parser: **7 passed**. The existing-output safety check rejected reuse while preserving all 279 files of the earlier audit.
- Authentic snapshot-4 input generation and deliberately synthetic casualty write/read passed at **25/50/75/100%**. Every run checked **88 exact source chunks**: 77 levy and 11 MAA, including four reserved equipment sources. Native current/capacity/cache totals, source identities, eligibility and unchanged equipment stock were checked; missing/duplicate/impossible reports failed without partial output. Profiles were 21/22/22/22 and formations 21/25/30/30. No personal campaign result or character fate was produced.
- Those final cohort replays use the frozen application snapshot recorded in their receipt. The subsequent XPath-only delta affects quoted lookup strings; the fixture's Briton/Latin/Western Germanic Rebels factions contain no quotes. The final all-era matrix and quote regressions exercise that delta separately. This is offline evidence, not a new native gameplay trial.
- RPFM encoded and decoded the final 100% replay's **10 generated tables and 176 rows**, preserving every data cell. The cache/schema checks and private generated records are separate from engine rendering/combat verification.
- The self-contained release was installed separately on 2026-10-22. Assembly SHA-256: `00A1F8C76342E660D677A928BA6C3A76970DF38797922FEB5821D4C9551EAE32`. All **402 published files** were hash-checked; **45 existing state/input/settings files** were verified unchanged. Neither game nor companion was launched. Generate a fresh battle for the user's next trial.

The [portable receipt](./broad-unit-mapping-audit.json) records aggregate results. Detailed source catalogs, personal saves, matrix rows and staged synthetic outputs remain outside Git. Earlier failed attempts remain separate evidence rather than being overwritten.
