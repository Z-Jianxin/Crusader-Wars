# Attila engine calibration fixture

This task-only generator creates an isolated historical battle pack and mod-list file in a new scratch directory. It does not launch Attila, write its installed packs, change its selected mods, or relax production mounted-HP restrictions.

## Build and prepare

Requires .NET 9, a completed Attila installation, and the installed Crusader Conflicts runtime (for RPFM/schema). Native source packs are boot/data and the selected locale. No overhaul or gameplay mod is enabled by the fixture.

```powershell
dotnet restore Tests/AttilaCalibration/AttilaCalibration.csproj --source https://api.nuget.org/v3/index.json
dotnet run --project Tests/AttilaCalibration/AttilaCalibration.csproj --no-restore -- '<CC runtime>' '<Attila root>' '<new scratch directory>' infantry 4
dotnet run --project Tests/AttilaCalibration/AttilaCalibration.csproj --no-restore -- '<CC runtime>' '<Attila root>' '<another new scratch directory>' mounted 4
dotnet run --project Tests/AttilaCalibration/AttilaCalibration.csproj --no-restore -- '<CC runtime>' '<Attila root>' '<native control scratch directory>' native
```

The generator selects a vanilla melee unit, verifies dependencies, builds private variants with the production backend, compiles both Lua scripts without executing them, packs the fixture, and verifies every generated TSV value after binary roundtrip. It records the source unit, source packs, generated stats, case keys and pair definitions in `manifest.json`.

The infantry suite has generated baseline versus baseline, native unit versus generated baseline, double damage versus baseline, and double infantry HP versus baseline. Four replicates produce sixteen independent lanes. The native comparison checks that creating a neutral variant has not itself changed combat. Odd/even replicates switch the treated side. The mounted suite has baseline, doubled man entity HP, doubled mount entity HP, and both entity HP fields doubled, producing sixteen lanes. The original land bonus HP is preserved in all mounted cases; these labels do not assume its contribution to effective mounted health. Mounted changes are explicit experimental edits to the fixture, not supported production behavior. Four replicates per case are pilot observations; repeat launches for a useful sample.

Each pair has sixty soldiers at zero experience, with matched orders and fearless morale. Both sides use the same faction. Remote invincible observer generals keep the battle alive while pairs finish. Fire-at-will is disabled and candidates exclude missile weapons. Pairs have spaced lanes and script controllers retain control after their opponent dies. Terrain is the fixed native Adrianople historical map, not a guaranteed flat map; repeated mirrored trials help identify positional bias but cannot remove all terrain effects.

## Launch contract

The operator/root agent owns all game launches. Ensure Attila is closed before changing fixture packs. Use the generated `used_mods_cc_calibration.txt` alone, with Attila's working directory set to its game root. The file has an absolute `add_working_directory` pointing to its scratch pack. If Attila does not accept an absolute mod-list command-line path, copy this task-specific text file to the game root and pass its filename; never replace `used_mods_cc.txt`.

After the normal frontend finishes loading, use **Historical Battles > Adrianople**. The fixture reuses that native registration and its terrain/environment, overriding its battle XML and pointing it at the calibration script. The frontend is the original native script with event logging; there is no startup timer. The battle script uses `bm:setup_battle`, matching the `prevent_deployment=true` path in Attila's `generated_battle` library. Note that `bm:end_deployment()` itself only releases input focus: it is not an engine start command.

The root operator owns the menu interactions. If the engine still waits on deployment, `battle.csv` records the `Deployment` phase; that is a harness failure to investigate, not a completed trial. Do not fabricate success from a generated pack. Scripts remain unverified in the actual engine until a real run emits `START`, paired samples and `COMPLETE`.

The `native` control suite contains only two native Lua files with added logging. It has zero generated stats, DB rows, XML, terrain, or gameplay changes, and also launches through **Historical Battles > Adrianople**. Its `native-control.log` records frontend/UI events and entry into the native Adrianople battle script. Use this to distinguish installation/frontend failures from calibration-fixture failures. It does not produce combat-analysis CSV and should not be passed to the analyzer.

### Prior failed bootstrap

The earlier `infantry-04` attempted `FrontEnd.start_historic_battle("tut_tutorial_battle")` from a five-second native frontend timer. In the actual run the timer became due only after the long introductory cinematic. The process died before returning from the launch call or loading the battle script. Windows recorded an `Empire.Retail.dll` access violation (`c0000005`, offset `00132b60`); the CA stack contained only unknown frames. These observations do not establish whether early launch timing, custom battle registration/XML, or another native issue caused the crash. The current control/menu workflow removes these simultaneous assumptions. `infantry-05` was generated with that same old bootstrap and should not be used for the revised path.

Logs go to absolute task-owned paths beside the fixture:

- `frontend.log`: native historical menu events.
- `battle.csv`: script load, phase changes, unit types, initial counts, one-second samples, pair completions, errors and completion.
- `manifest.json`: immutable fixture definitions and expected records.

The operator may close Attila after `COMPLETE` appears. The script also attempts to trigger the normal results screen. A run times out after 300 battle seconds. Repeated launches append a distinct timestamp run ID. Do not run the same fixture concurrently, because simultaneous writers would share its log.

```powershell
python Tests/AttilaCalibration/analyze.py '<scratch directory>'
```

The analyzer reports mirrored treated/control survivor margins, durations, incomplete runs and possible cross-lane contamination. A run contributes valid trials only after START, COMPLETE, all expected lanes, correct actual unit keys, and initial/final samples are present without script errors. Every initial unit must actually contain sixty men with zero kills; otherwise unit-size scaling or premature combat invalidates that fixture. Kills must match the paired opponent's losses, including the final sample after a pair finishes. A positive treatment margin is useful evidence, not proof of an exact multiplier or deterministic battle result. Run `python -m unittest discover -s Tests/AttilaCalibration -p test_analyze.py` to verify these evidence checks.

## Required remaining observations

1. Confirm native-control gameplay, then the calibration menu/deployment path, work in the installed build.
2. Confirm initial unit keys/counts and unchanged models/abilities. Native Lua supplies alive/killed counts; no verified native per-unit max-HP getter is assumed.
3. Compare multiple infantry baseline/damage/HP runs, including mirrored controls and timeouts.
4. Compare mounted man/mount/both changes and repeat after dismounting before enabling mounted toughness in production. The current suite starts mounted and does not settle dismounted casualty conversion by itself.
5. Run separate deliberate conflicting-pack/fragment fixtures before claiming source-load-order parity with the engine.

Successful compilation and RPFM roundtrip establish fixture/schema compatibility only. They are deliberately distinguished from engine execution and combat validation.
