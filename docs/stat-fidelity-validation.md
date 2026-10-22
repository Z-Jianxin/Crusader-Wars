# CK3 combat-stat build: accepted battle and build validation

The corrected **v4** import passed a fresh native CK3 load, paused resave and one real game day through battle completion. The final self-contained Windows build reached its Ready screen and closed normally with both child `DOTNET_ROOT` variables cleared and an unrelated working directory.

The [structured receipt](./stat-fidelity-validation.json) records hashes, test counts and limits. The [usage guide](./using-ck3-stat-fidelity.md) explains the separate runtime and explicit result-save loading. Raw campaign saves, logs and machine paths remain outside Git.

## What was actually exercised

The application handled a fresh normal Fight export, conversion, pack installation and Attila launch. Native Continue Battle regenerated the same snapshot after test-controller repairs. The real battle ended naturally in **Defeat**, with two complete survivor phases and two complete kill phases covering all **51** deployed formations. Its completion marker was controller counter 1,380, not a calibrated simulation-time measurement. The temporary controller used ordinary movement and attack orders; it did not force a winner, health, morale, stats or casualties, and is absent from the installed build.

Independent inspection rejected the original application output. The corrected production importer was then replayed against that exact native input, actual deployed XML, actual AAR and the hash-bound original first-pass character decisions. This avoided silently rerolling injury/capture/death. The resulting v4 save was loaded and advanced natively. This is a corrected replay of one real battle, not a claim that a second battle was fought on the final binary.

| Quantity | Attacker | Defender |
|---|---:|---:|
| Actual Attila deployment | 8,002 | 4,892 |
| Actual Attila final survivors | 2,975 | 263 |
| Returned CK3 paused troops | 2,973 | 261 |
| CK3 troops after native completion | 2,973 | 260 |

Attila general guards and partial siege crew are representations rather than CK3 troops. Native knight retinues return to individual characters. The five original decisions were four wounded, one maimed, three captured and **no deaths**; all survived serialization, native load and completion. Two uncaptured wounded knights were initially available. One then left the army alive to travel home, explaining 261→260. Native AI regrouped the attackers into 2,005 and 968 without changing their combined strength. All three named commanders retained their health, traits and death/capture state.

## Save and completion invariants

- All 79 participating source records and exact chunk identities remained bounded; 103 unselected chunks and compact-source metadata were preserved. Unrelated source/character records and gameplay/achievement flags passed the independent audit.
- Paused native resave preserved source counts, knight outcomes, commander state and all 13 pending result groups. After one day, 78 remaining source chunks were unchanged; native cleanup removed the zero army and its zero source chunk.
- CK3 completed the combat, added **exactly one** battle-history entry and awarded the winning side **50** war score. The importer preserves prebattle war state and leaves reward/date/winner summaries to native completion.
- All 13 `final_count` accumulators started at zero and were populated **once** by CK3. Supplied main/pursuit kill and loss values, original counts and other existing report fields remained unchanged.
- Kill credits are actual raw Attila credits, distinct from native troop losses. The five generic-guard credits have no native troop row and are explicitly excluded. The 206 pooled knight credits use deterministic modeled shares; individual character kills were not observed.
- Native save-prefix bytes and declared metadata length were preserved exactly, and ZIP CRC checks passed. Save-body line-ending normalization is distinct from the byte-sensitive metadata prefix.

## Rejected attempts retained as diagnostics

| Attempt | Why it was not accepted |
|---|---|
| Original application import | Incorrect fallen-knight death handling, collapsed levy chunks, lost compact metadata, stale caches/totals and a rounding overflow. |
| Corrected v1 | Source/fate payload passed, but the offline harness changed metadata line endings without updating its declared byte length. Native loading failed. |
| Corrected v2 | Exact-prefix repair loaded correctly and preserved troops/fates; native completion exposed a duplicate manual war-score award and stale report counters. |
| Corrected v3 | Single native award and correct losses/credits, but prefilled `final_count` values were added again by native completion. Actual troops/fates were intact. |
| Corrected v4 | Accepted native load, resave and completion with one award and correctly populated report accumulators. |

No rejected result is the supported returned campaign save. The exact-ID regression also prevents regiment 6 from matching regiment 65. Missing bound combat/side data and incomplete native AAR rows abort fidelity import rather than publishing a partial return.

## Final build and checks

| Suite | Passed |
|---|---:|
| [Model](../Tests/StatFidelity.Model.Tests/StatFidelity.Model.Tests.csproj) | 85 |
| [Application integration](../Tests/StatFidelity.Integration.Tests/StatFidelity.Integration.Tests.csproj) | 99 |
| [Database backend](../Tests/StatFidelity.Backend.Tests/StatFidelity.Backend.Tests.csproj) | 16 |
| [Calibration analyzer](../Tests/AttilaCalibration/test_analyze.py) | 8 |

The package includes .NET and Windows Desktop 9.0.21, retains the enabled fidelity option, disables upstream self-replacement, and publishes separate result saves. The original installed runtime was preserved. Its final assembly SHA-256 is `C648350F08EC4356B82EFECF23C5E9501E84197E3E3260A897A3CEF4ADF17174`.

The independent [CK3 exporter/observer receipt](./ck3-runtime-validation-20261021.md) covers native getter probes and the frozen copied-playset long run; it is separate evidence. Reproduction details are in the [actual replay harness](../Tests/StatFidelity.BattleHarness/README.md), [native runtime workflow](../Tests/DevelopmentRuntime/README.md), and [calibration workflow](../Tests/AttilaCalibration/README.md).

This remains experimental cross-game translation. It validates direct field effects, exact evidence binding and the tested native return. It does not prove identical CK3/Attila battle outcomes, empirical DPS equivalence, every mod combination, full terrain/counter/pursuit/screen coverage, special-entity HP, or dismounted combat. The overhaul hides the tested knight health row: its live card proved bodies, damage/AP and XP0, while HP evidence comes from the separate native component tests and generated database checks.
