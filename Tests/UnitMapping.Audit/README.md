# Offline unit-mapping audit

This tool checks the actual DefaultCK3 resolver against the active CK3 registry and effective installed Attila database. It never launches a game, writes the live Attila mod list, imports battle results or changes a campaign.

First build a native catalog with Python 3.10 or newer:

```powershell
python Tests/UnitMapping.Audit/native_catalog.py --ck3 $ck3Exe --ck3-user-dir $ck3UserDir --out $newCatalogJson
python -m unittest discover -s Tests/UnitMapping.Audit -p 'test_*.py'
```

The catalog follows ordered active descriptors, file replacement and `replace_path`. It records native troop roles, participation flags, base stats, cultures, heritages, innovation/era gates and input hashes. Conditional recruitment triggers remain unevaluated; missing data is not guessed.

Build the .NET 9 Windows console project and run it with a genuine, matching handoff and extracted native living section:

```powershell
dotnet build Tests/UnitMapping.Audit/UnitMapping.Audit.csproj
dotnet Tests/UnitMapping.Audit/bin/Debug/net9.0-windows/UnitMapping.Audit.dll `
  --runtime $companionRuntime --ck3 $ck3Exe --ck3-user-dir $ck3UserDir `
  --attila $attilaExe --native-catalog $newCatalogJson `
  --context-handoff $nativeHandoffLog --context-living $nativeLivingFile `
  --context-player $nativePlayerId --context-combat $nativeCombatId `
  --cache $externalCacheDir --out $newAuditDir
```

The native evidence enables the same save-bound policy used by production. The queried units and cultures are synthetic resolution probes, not a fabricated army or proof that they participated in that battle. Never invent a handoff to satisfy this requirement.

Output must be a new directory outside the companion, games and CK3 user directory. The cache must also be external; it can reuse completed extracted packs. Installed RPFM/schema files and pack data are read-only inputs. An optional `--era FOLDER` selects one exact mapper folder name; an unmatched filter fails.

The current audited configuration is **DefaultCK3 with DahanChina**, plus each era's mandatory packs, including Earl's three later-era packs. Manual extras and other playthroughs are outside this configuration. Pack selection is explicit and never changes the live battle stack. Identical later-era physical stacks share one prepared database.

Per era, the output records:

- Every effective authored faction/title row, including scoped overlays, and metadata for all referenced assets.
- Every static native culture, each heritage-only dynamic-culture fallback, explicit Default and fully unknown fallback.
- Each active native type through every faction reached by those contexts, using the real resolver. Unreachable authored factions are inventoried rather than exhaustively resolved.
- Positive formation capacity for types using the category fallback, so a valid fallback key cannot hide a zero-limit source loss. Explicit authored limits remain inventoried separately.
- Actual man/mount/engine/projectile references, mount family, weight, ammunition and precursor status, visual-reference existence, missing assets and HP-representation support.
- Resolver rejections and semantic findings, kept separate from authored-candidate reviews. A title candidate inventory does not exercise every possible title owner.

Input and assembly hashes identify the run. A stale catalog's mod-list/type mismatch fails. Missing asset and coarse role checks are useful evidence; visual-reference existence is not proof that models render correctly. Melee/ranged hybrids, equipment weight and special native families need contextual review. Counts are potential resolution cases, not counts of recruitable units or observed broken battles.

See the [broader audit report](../../docs/broad-unit-mapping-audit.md) for verified fixes, remaining unsupported cases and the separate captured-native casualty replays. Full CK3 combat equivalence, soft-casualty recovery, counters, terrain and pursuit remain outside this audit's claims.
