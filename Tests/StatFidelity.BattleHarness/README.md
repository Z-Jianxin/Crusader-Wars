# Offline battle replay

This executable replays a **real CK3 handoff save and its matching console-log export** through the production sanitizer, stat parser, save reader, army reader, unit mapper, and battle XML/script generators. It provides an intermediate check before an Attila UI test. It does not launch or close games, import battle casualties, update campaign saves, or change installed runtime files.

The project targets .NET 9 on Windows. Restore with the .NET 9 SDK:

```powershell
dotnet restore Tests/StatFidelity.BattleHarness/StatFidelity.BattleHarness.csproj --source https://api.nuget.org/v3/index.json
dotnet run --project Tests/StatFidelity.BattleHarness -- --help
```

Run from the repository with paths to the installed Crusader Conflicts runtime, the captured handoff save, and the matching handoff log:

```powershell
dotnet run --project Tests/StatFidelity.BattleHarness -- `
  --runtime 'C:/installed/crusader-conflicts' `
  --save 'C:/test-evidence/handoff.ck3' `
  --log 'C:/test-evidence/console.log' `
  --out 'C:/test-evidence/replay-01' `
  --mapper DefaultCK3
```

`--out` must not exist and must be outside the installed runtime. Inputs and selected runtime assets are copied there. Plaintext debug saves and ZIP-based saves are supported; only the archive's `gamestate` entry is extracted. Logs need the ordinary Crusader Conflicts battle fields and a complete `CCSTAT|v1` snapshot inside the same latest `CCHANDOFF|v1|BEGIN` / `CCHANDOFF|v1|END` group. A missing current field or incomplete latest group fails rather than taking an older value. An export by itself cannot replace the battle context. Never add fabricated boundaries or headers to an old fixture to make it pass; use `--snapshot-only` for older native evidence.

The default replay enables strict stat export/coverage checks, disables automatic battle scaling, and uses a 100% soldier count. The snapshot serial must match the selected player's save variable, and the saved combat reference must match the combat selected by the production reader. A stale, missing, incomplete, or mismatched export is a failed replay. Field battles are currently supported; sieges are explicitly rejected by this harness.

Optional arguments:

- `--export-only`: stop after the real save/army/mapper readers and strict source-regiment coverage validation. This isolates export/identity failures from battle-layout generation. `profiles.json` contains planned regiment profiles only; no battle or database is generated.
- `--snapshot-only --player-id ID`: validate a native CCSTAT frame against the saved combat and source-regiment/knight roster without requiring the legacy GUI handoff fields. It uses the production parser/save readers, does not invent missing log fields, and emits `native-coverage.json`. It does not validate mapping or generate a battle.
- `--ck3-user-dir DIR`: select the CK3 user directory whose active mod definitions the army reader needs. The default is `Documents/Paradox Interactive/Crusader Kings III`. The harness reads its active-mod/landed-title definitions and snapshots its launcher mod list; it does not write that directory or persist application settings.
- `--stat-settings FILE`: use an explicit fidelity configuration instead of strict defaults. The selected configuration is copied into the output. Use it to exercise the experimental knight calibration; it is not proof of balance.
- `--attila EXE`: also read the installed Attila pack stack and generate unit/weapon database TSV variants in the isolated battle folder. This does not install a pack or launch Attila.
- `--stat-cache DIR`: reuse an already completed extraction cache for the prepared active Attila database, including anchored knight planning. Active pack paths, order, lengths and modification times are still checked. Anchored planning reads the installed active database even without TSV generation; `--attila` explicitly selects its executable and requests generated tables. Planning and publication use the same prepared database.
- `--casualty-smoke`: generate explicitly synthetic survivor reports from the real generated unit XML, reject missing/duplicate/malformed reports, and exercise the production casualty allocator plus save writers/readers entirely inside the output directory. The resulting `roundtrip.synthetic.gamestate` is never installed or loaded by a game; it is a serialization test, not a measured battle outcome.

Outputs include `inputs.json` with hashes of the copied evidence, `result.json` with the completed/failed stage, `data/debug.log`, `data/stat_fidelity_coverage.json`, `profiles.json`, `armies.json`, and the generated files under `data/battle files`. A successful replay proves offline conversion only. Engine loading, visible stat changes, battle outcomes, and actual casualty return still require separate in-game tests.

The latest default-settings reference is **levy02-production-handoff**, replayed as `harness-levy02-defaults-v2`. It passed with native snapshot 4, exact combat/result link 0, one attacker and three defender armies, six MAA records, three owner-levy rows and five knights. All 26 generated profiles were built against the full active database; anchored mounted knight conversion and complete levy coverage were enabled by default, without an override file. Pure-levy allied army 30 contained 750 soldiers and no native commander. Its generated general is untracked: normal unit-survivor reports exist, while no character-death hook is emitted. The isolated synthetic casualty roundtrip preserved five exact non-siege MAA sources and unchanged achievement eligibility.

The native levy ratios were 1.344 toughness for attacker owner 33385, 1.11 for defender owner 32396 and 0.96 for allied defender owner 32570; the attacker received its separate 1.3 commander damage factor once. This run retained the current Chinese battle title and England/Normandy realm names. Save SHA-256: `98797D798094CE66CDFA110C3342EB8CDA6D4BE4A7C4D398F3557D5D44FB70A6`; log SHA-256: `6FF9702793D5F84F21FDF09541CE9656DE8A3C70638FD888A9D6579BD36FD798`.

Redirect stdout/stderr to a task-owned sibling log: the inherited save reader emits very large debug output. Inspect `result.json` and the end of that log instead of flooding a terminal or agent tool response.

The current clean full-conversion fixture is **station03/framed-handoff**, captured on 2026-10-21 from the native Chinese UI. Its actual save is dated 1066-09-30, native snapshot 3, combat 0, attacker army 24 and defender army 18. `harness-station03-framed/result.json` records 15 profiles, three character formations, source-origin validation (huscarl 17620, station 1527), and a successful isolated synthetic casualty roundtrip with negative report checks and preserved achievement eligibility. Input save SHA-256: `40D02C2B465B5B94F66C12AFAABAC91817E46598B5F95B33422FB7C100B5F376`; log SHA-256: `DF009830CE55F28FD2D3A3A2408E53DBE61C91D542DAD1960D2A2C983DEE71F1`.

The follow-up `harness-station03-framed-anchored-foot-v2` used an explicit test-only foot representation, active pack-stack database generation and the same original framed input. It passed with 16 profiles and 31 abstract retinue bodies split across four formations. Its result records parsed date 1066-09-30 and the selected current context; `armies.json` includes source origins. This run also found and fixed an inherited CRLF commander-name bug that emitted malformed unit-card localization. Generated TSV fields now preserve quotes, tabs, line breaks and Unicode correctly.

The following combat02 runs are historical pre-framing evidence. Their native snapshot/save/source checks remain useful, but the old legacy first-match reader could borrow context from older handoffs. Current full conversion intentionally rejects their unframed histories. The old four-body knight run is an explicit template-relative reference test; it is not the new anchored budget or proof of balance.

On 2026-10-21 the real CK3 1.20.1.2 `combat02` snapshot-3 handoff completed offline conversion: player 32396, combat 0, two armies, three non-siege MAA profiles, five native knights, and 15 generated unit profiles. A second replay with explicit knight calibration generated 16 profiles: the five knights' total prowess 64 and efficiency 2.35 produced damage/HP multiplier 3.008 on 20 bodies with XP 0. Each general had two bodies and XP 0. The real mangonel record has base damage 0 and was correctly excluded from field-stat ratios by the mapper's siege classification.

Input save SHA-256: `A1406DB06957FAF1D1B526BF9F7B4DC8FC91B7287DFB907B7DE10E7AE50D53FE`; log SHA-256: `4662F59D84F44FE4BC114E3D7BACA5A4204192ABEF3B99D95C0B365CF0197626`. Native snapshot-2 also passed independent save binding and exact source coverage. These are real save/log replays, not synthetic export records. The chosen mapper was `OfficialCC_DefaultCK3_EarlyMedieval_919Mod`; database pack generation and in-engine battles remain separate checks.

The optional synthetic casualty test also passed on this real conversion: armored footmen 1,000 → 500, bowmen 1,000 → 750, and huscarls 400 → 100 were serialized into a full isolated gamestate and re-read exactly. Missing, duplicated and malformed source reports each failed before source-count mutation. The original isolated input gamestate hash was unchanged. These deliberately artificial survivor counts do not establish real Attila casualty behavior or campaign compatibility.

Fidelity-mode writeback preserves the input achievement eligibility instead of applying the inherited legacy enablement. The latest real-fixture smoke checked one `can_get_achievements=no` flag and preserved it unchanged; separate regression cases compare the complete minimal input/output bytes for both `yes` and `no`. Army-regiment totals also matched their re-read source chunks.

The later real **snapshot-6 reinforcement fixture** also completed full offline conversion with knight calibration: one attacker army, three defender armies, three owners, eight exported MAA records (seven non-siege), and 20 generated profiles. Owner 32570's four sources `16793755`, `16793756`, `16793757`, `16793760` remained distinct across armies 35 and 36. Both 600-soldier armored regiments split into two 300-soldier Attila units. Artificial casualty reports then re-read 150, 200, 450 and 100 survivors for those four sources respectively; the two same-type, same-owner armored regiments correctly retained different losses. Source counts, army-regiment totals, input hash and achievement eligibility all passed.

Snapshot-6 input save SHA-256: `845A21672431B891000CFEB0B36A0795EA6F9B6E2A5DD85F5CB730345BFC9520`; log SHA-256: `E8BFCAC69C1BF7C844D925A6A3EE08FB66564A053DCA8366DFF160FAA8C7600E`. The older snapshot-5 exporter omitted those four allied sources; replay rejected it at the first absent source before generating a battle. This provides a real negative/positive pair for reinforcement coverage.
