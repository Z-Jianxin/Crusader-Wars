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

`--out` must not exist and must be outside the installed runtime. Inputs and selected runtime assets are copied there. Plaintext debug saves and ZIP-based saves are supported; only the archive's `gamestate` entry is extracted. Logs need the ordinary Crusader Conflicts battle fields as well as a complete `CCSTAT|v1` snapshot. An export by itself cannot replace the battle context.

The default replay enables strict stat export/coverage checks, disables automatic battle scaling, and uses a 100% soldier count. The snapshot serial must match the selected player's save variable, and the saved combat reference must match the combat selected by the production reader. A stale, missing, incomplete, or mismatched export is a failed replay. Field battles are currently supported; sieges are explicitly rejected by this harness.

Optional arguments:

- `--export-only`: stop after the real save/army/mapper readers and strict source-regiment coverage validation. This isolates export/identity failures from battle-layout generation. `profiles.json` contains planned regiment profiles only; no battle or database is generated.
- `--stat-settings FILE`: use an explicit fidelity configuration instead of strict defaults. The selected configuration is copied into the output. Use it to exercise the experimental knight calibration; it is not proof of balance.
- `--attila EXE`: also read the installed Attila pack stack and generate unit/weapon database TSV variants in the isolated battle folder. This does not install a pack or launch Attila.

Outputs include `inputs.json` with hashes of the copied evidence, `result.json` with the completed/failed stage, `data/debug.log`, `data/stat_fidelity_coverage.json`, `profiles.json`, `armies.json`, and the generated files under `data/battle files`. A successful replay proves offline conversion only. Engine loading, visible stat changes, battle outcomes, and actual casualty return still require separate in-game tests.

As of 2026-10-21 the harness builds and has rejected the real non-battle probe04 save/log for missing combat export as expected. A positive replay must be recorded separately with a real combat handoff; a synthetic stat record is not sufficient evidence.
