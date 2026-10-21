# Attila stat variant backend

The backend creates private Attila database records for the profiles accepted by the CK3 exporter and mapper. It changes base normal/AP weapon damage and infantry effective hit points before Attila starts. It does not use unverified Warhammer Lua stat APIs, modify existing game packs, or promise deterministic combat outcomes.

## Entry points

`CrusaderWars.StatFidelity.UnitStatProfile` contains an evidence identity, mapped base unit key, damage multiplier and hit-point multiplier. `StatFidelityBackend.GeneratedKey(profile)` produces the same stable `cc_sf_` key that `Generate(...)` writes. The mapper registers profiles while producing battle XML; `PackFileCreator` materializes their records before packing. Profile conflicts, non-finite/non-positive factors, integer overflow and unresolved dependencies abort the new battle. Factors are never silently capped by this backend. Integer fields round to the nearest integer, halfway away from zero; fewer than one resulting hit point is rejected.

Sources: [profile/key](../CrusaderWars/packfile/stat_fidelity/UnitStatProfile.cs), [backend](../CrusaderWars/packfile/stat_fidelity/StatFidelityBackend.cs), [variant builder](../CrusaderWars/packfile/stat_fidelity/StatVariantBuilder.cs), [pack publication](../CrusaderWars/packfile/PackFile.cs).

## Active database

The source resolver includes PFH4 boot/release/patch packs, explicitly enabled packs from `used_mods_cc.txt` in reverse file order, and automatic movie packs. Crusader Conflicts lists its highest-priority mod first. The old generated `CrusaderConflicts.pack` and temporary build packs are excluded. Missing or ambiguous enabled pack names fail rather than selecting an arbitrary copy.

Localization follows `data/language.txt` (Attila's two-letter codes), with `local_en_shared_rome2.pack` below the selected locale as a fallback. If the file is absent, a single installed locale can be inferred; several installed locales cause an explicit error. Other language packs are excluded. Per-battle localization prepared by Crusader Conflicts is applied last.

Pack-file priority is resolved **before database rows**. Higher-priority packs replace entire files at the same virtual path. A row omitted by a replacement file must not survive from the overwritten file. Remaining fragments are processed in source priority order and descending filename order so earlier fragments win within a source. TSV column order and schema versions are preserved, and incompatible definitions fail explicitly.

The whole-file behavior follows RPFM's own [pack merge implementation](https://github.com/Frodo45127/rpfm/blob/master/rpfm_lib/src/files/pack/mod.rs): `read_and_merge` groups packs by type, reverses their priority, and extends the virtual-path map with each pack's files. Its [dependency implementation](https://github.com/Frodo45127/rpfm/blob/master/rpfm_extensions/src/dependencies/mod.rs) also explicitly replaces vanilla files with parent files. [Attila game metadata](https://github.com/Frodo45127/rpfm/blob/master/rpfm_lib/src/games/supported_games.rs) includes Boot and the language-specific packs. This is tool-source evidence; duplicate-fragment priority still needs an in-game Attila fixture with deliberately conflicting rows to establish engine behavior independently.

## Generated records

Each profile gets unique `main_units` and `land_units` keys. Melee weapons are cloned and both `damage` and `ap_damage` are scaled. Missile weapons, their default projectile, and every alternate-ammunition junction/projectile are cloned. Artillery engine missile chains are supported by the table builder, although the CK3 mapper can reject siege profiles when CK3 siege progress cannot justify field damage.

For ordinary infantry, effective HP is `battle_entities.hit_points + land_units.bonus_hit_points`. The backend assigns the scaled total to a private man entity and zeroes the cloned unit's bonus HP. This supports reductions without negative bonus HP. It preserves attack/defence, armour, shields, animation, movement, formations, abilities, shared variant assets, card images and localized names. Existing weapon contact effects, projectile explosions and scripted/synchronized kills are retained rather than reinterpreted as CK3 damage; those mechanics can change effective lethality independently of the scaled damage fields.

**Mounted, chariot, animal and articulated-unit toughness remains uncalibrated.** A profile requesting a non-identity hit-point multiplier for these units fails with an explicit calibration-pending diagnostic. Changing a rider's man entity alone is not treated as proof of mounted casualty durability. Damage-only profiles can retain the original HP. Do not describe cavalry toughness as supported until controlled mounted/dismounted casualty tests establish the effective entity formula. Naval profiles are rejected.

The builder resolves every record it needs to change, plus the base visual rows, localization and ability junctions. Unchanged referenced assets remain provided by the active mods; this is not a full missing-mesh/animation/asset validator.

## Cache and publication

Cache buckets include source path, size, timestamp, schema SHA-256 and cache-format version. Every extraction uses a new `attempt-<guid>` directory. A completion marker selects only a fully decoded attempt. Incomplete attempts are never recursively deleted. Cache root/bucket/attempt directories, markers and descendants reject links/reparse points. Old failed attempts can be inspected or removed manually after the application has stopped.

Generated tables are published only after all profiles validate. RPFM commands use structured argument lists, drain stdout/stderr concurrently, enforce a timeout and check exit codes. A fresh pack is built beside the destination, verified readable, and atomically replaces the previous pack. A backend failure leaves the existing production pack unchanged and records the diagnostic in the coverage report.

## Validation

Run the focused project with a .NET 9 SDK:

```powershell
dotnet restore Tests/StatFidelity.Backend.Tests/StatFidelity.Backend.Tests.csproj --source https://api.nuget.org/v3/index.json
dotnet test Tests/StatFidelity.Backend.Tests/StatFidelity.Backend.Tests.csproj --no-restore
```

The [tests](../Tests/StatFidelity.Backend.Tests/BackendTests.cs) cover independent opposing profiles, damage/AP and alternate ammunition, effective infantry HP, abilities/visuals/names, row priority and whole-file replacement, schema rejection, missing-reference publication failure, bounds, TSV quoting, load-order directories, Boot/localization selection and mounted-HP rejection.

To enable the real pack roundtrip test, set `CC_RPFM`, `CC_SCHEMA`, and `CC_TEST_PACK` to a compatible RPFM CLI, Attila schema and an installed overhaul pack. It finds a self-contained eligible infantry row, clones it, builds a scratch pack, extracts it again and checks the resulting records. It also verifies that an interrupted-cache marker causes a fresh attempt without deleting the previous attempt. This test has been run with RPFM 3.99.109 and the residual AD919 compatibility pack; no production pack is changed.

RPFM success proves binary/schema compatibility, not engine combat behavior. Before releasing an enabled profile, verify both factions' generated stat cards, visuals and ability lists; test identical infantry matchups across damage-only and HP-only changes; verify alternate missiles; test deliberate pack/fragment conflicts; and record repeated battle outcomes. Mounted toughness, special projectiles and nonlinear attack/armour interactions need separate calibration. Attila build 25370302 is now installed and its `data/language.txt` is `EN`; the native Boot pack contains startup graphics rather than the combat tables. The task-only [calibration harness](../Tests/AttilaCalibration/README.md) builds verified vanilla fixtures and logs actual runtime observations. Combat validation remains pending until its battle log records completed trials.
