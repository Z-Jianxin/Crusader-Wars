# Default unit pack selection validation

The 2026-10-21 companion update enables **Dahan China** for a previously unconfigured Default CK3 playthrough. Explicit user choices, including disabling every submod, remain saved. Switching playthroughs preserves their individual choices.

**Earl's Realistic Units** remains a mandatory three-pack requirement of the High, Late, and Renaissance mappers. A source-controlled boundary policy ends official Early Medieval at 1129 and starts High Medieval at 1130, preventing the old inclusive overlap from combining the 919 and 1212 stacks. Custom and unrelated playthrough ranges retain their configured behavior.

## Verified behavior

- All **132 application integration tests** pass: the previous 99 plus seven default/persistence cases, eighteen era-policy cases, and eight dependency/discovery cases.
- A read-only probe used the production selection APIs, a scratch copy of the installed mapper catalog, and actual installed pack discovery. It wrote no game launch files and launched no game.

| CK3 year | Selected era | Required packs | Earl packs | Dahan variant |
|---|---|---:|---|---|
| 1066, 1129 | Early | 10 | Excluded | Original main + English patch |
| 1130, 1178 | High | 17 | All three | CC fix + English patch |
| 1260 | Late | 17 | All three | CC fix + English patch |
| 1370 | Renaissance | 17 | All three | CC fix + English patch |

Every selected dependency was present. The missing [Dahan compatibility pack](https://www.nexusmods.com/totalwarattila/mods/25?tab=files) was downloaded and installed without replacing an existing file. Its extracted SHA-256 matched the mapper author's reference exactly: `e9325e22cbefe760832aad3982270044a4fc0ac43db84322abe5a6f44f32fa75`. The Nexus mod 24 page is the AD919 compatibility pack, not Dahan's.

The self-contained application was updated and restarted. Its native Ready screen displayed **Dahan China** under active optional submods, and the selection persisted to settings. Other saved options, paths, mapper selection and manually selected extras remained byte-identical to their state immediately before the restart. No CK3 campaign or launcher settings were changed.

Installed application assembly SHA-256: `AA682EB18719E855416A41B7F2D0D95AAB900BB68C4D5EEE17994A85A086E4FA`.

## Dependency handling and limits

The launch preflight checks the effective current-era requirements after submod replacements, before modifying either startup file. Missing or removed packs produce an error instead of a partial load list. Discovery includes every pack in each Workshop item, and duplicate copies of manifest-managed packs cannot enter through the manual-extra list in an unrelated era. Failed era selection clears the preceding mapper state.

Presence is separate from version compatibility. The installed Earl packs and Dahan original main differ from the mapper's pinned hashes; those pins were preserved, and the explicit verifier continues to report mismatches. This update verifies selection, dependencies, persistence and companion UI. It does not claim a new battle calibration or full native battle return with those changed Workshop versions. The earlier [combat-fidelity validation](./stat-fidelity-validation.md) remains a separate historical receipt.

See the [usage guide](./using-ck3-stat-fidelity.md#default-attila-unit-packs) for source links and the distinction between automatic packs and the general Mod Manager checkboxes. Raw receipts, local paths, settings backups and downloaded assets remain outside Git.
