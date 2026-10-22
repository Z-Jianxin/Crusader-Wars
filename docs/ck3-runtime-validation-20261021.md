# CK3 runtime validation — 2026-10-21

The copied 45-mod playset completed a rendered observer campaign from
**1066.9.15 to 1081.2.23**, followed by native save/load at the same date.
Every file in the enabled mod roots and every launcher wrapper matched the
prelaunch content hashes afterward. This is bounded CK3 validation with
documented diagnostics, not an error-free or universal compatibility claim.

The [structured receipt](./ck3-runtime-validation-20261021.json) records the
counts and hashes. Raw saves, logs, launcher state and machine paths remain
outside Git. The [disposable probe procedure](../Tests/ck3_probe/README.md)
documents the executable tests and their limitations.

The later completed Attila battle and native CK3 return are documented in the
separate [stat-fidelity validation receipt](./stat-fidelity-validation.md) and
its [structured record](./stat-fidelity-validation.json). This page retains the
bounded exporter/observer scope and the source hashes for that run.

## Environment and source boundary

- CK3 **1.20.1.2**, Steam build **26046173**, normal Windows rendering.
- Copied playset: `vanilla-20261013-crusader-conflicts`, 48 entries / 45 enabled.
  Original membership, enabled flags and relative order were preserved; CW is
  immediately before the existing compatibility patch. Disabled DLCs remained
  unchanged. The debug probe was excluded from the observer run.
- Observer-run fork checkpoint: `4e1e3889c376c2cf844b7269154945255d87e25d`.
  Mega-repository checkpoint: `2ed2e6fd8ce797dc350f30e464e23ac02827e86d`.
- SHA-256 was calculated from all **25,646 files**, totaling **7,863,516,669
  bytes**, in the 45 enabled roots, plus all 45 wrappers. The post-exit comparison
  found zero additions, removals or content changes. This is an environment
  audit digest, not a portable build checksum.

```text
0cdff0ce71dc8466106b5493d58d0eb8ec52615ed999aeae454c64badbc336c6
```

The final process used a fresh 1066 bookmark with `d_munster`, `-debug_mode`,
`-nofilewatcher`, `-suppress_error_log`, and a unique log prefix.
`-suppress_error_log` suppresses the automatic log window; logging remained
enabled. The built-in Switch Character → Observe UI selected observer mode.

## Targeted exporter evidence

| Executed check | Observed result |
| --- | --- |
| Owner modifier apply/remove | Damage 40 → 60 → 40; toughness 27.56 → 39.96 → 27.56; knight effectiveness 1.85 → 2.35 → 1.85; commander advantage 19 → 24 → 19. |
| Native stationing and province-only modifier | London station 1527: 52/35.36 → 91/55.76 → 52/35.36, with the same source regiment and owner. |
| Native levy window versus independent empty-type context | Both returned damage 10 and toughness 11.1 → 5.55 → 11.1 for a levy-only −5 modifier; MAA control and native base 10/10 stayed unchanged. |
| Fresh allied reinforcements | A participant-based collector missed real armies and strict replay rejected them. The location/side roster then exported all owners and source identities. |
| Current handoff framing | A real `CCHANDOFF` group contained all 25 required current header fields exactly once, with its completed numeric snapshot and matching native save. |
| Production levy/MAA handoff | Snapshot 4 contained three levy owner profiles, stationed MAA, allied MAA and a separate 750-man levy army. Full offline conversion produced 26 profiles across four armies; synthetic casualty serialization passed. |

The 750-man supporting army had no commander and received an untracked generic
general in conversion, preventing its owner's death/capture from being inferred
from that generic unit. This does not exercise the distinct case of an absent
*side commander*. Its guarded header branches have source/type validation only.
The casualty serialization test used explicitly synthetic survivor reports.
The later native Attila outcome and corrected game-to-game return have their own
[evidence and limitations](./stat-fidelity-validation.md); they are not inferred
from this synthetic test.

## Observer and save/load evidence

The accepted run (`ccf_full45b_`) started normal simulation at
2026-10-22 01:06:24 UTC, paused at approximately 01:12:06 UTC, and had its endpoint
receipt recorded at 01:13:06 UTC. The game advanced **5,275 calendar days**:
14 years, 5 months and 8 days. Speed 5, observer status, Ireland/Britain terrain,
war units and the endpoint date were observed in the native UI.

The native local save `_1081_02_23.ck3` did not previously exist. Its ZIP CRC
passed, both outer metadata and gamestate reported `1081.2.23`, and its size was
25,679,881 bytes. SHA-256:

```text
ccee0bba5751bff0980c95081c7df83e4f40384a1e84ace74d4239759703d0b3
```

The native Load Game UI opened that save. The character-selection screen and
subsequent paused observer HUD both showed **1081.2.23**, with the world rendered.
The authorized test process was then stopped for cleanup. No crash was observed
during simulation or reload; this was not a natural Exit to Desktop test.

| Log boundary | Error records |
| --- | ---: |
| Initial stable startup | 2,064 |
| Paused observer endpoint, before save/load UI | 3,089 |
| After save/load UI and process cleanup | 3,374 |

The initial log prefix remained intact. Error output grew during simulation and
stayed well below the engine's observed 100,000-record ceiling. The closed error
log ended with a newline. Some debug/info logs retained partial buffered tails
after `Stop-Process`; the earlier paused-endpoint capture remains separate.
Complete multiline diagnostic blocks were compared with supplied baselines,
removing only leading timestamps. Dynamic IDs, line numbers and full stacks were
retained. Historical baseline presence is not proof that an error is harmless.

## Findings and limits

- **Rejected first attempt:** entering observer mode with title/county views open
  caused Holding Manager controls to evaluate an invalid player repeatedly.
  That attempt reached exactly 100,000 errors and supplies no uncapped-soak
  evidence. Closing both views before observing avoided the flood in the
  accepted run. Neither Holding Manager nor the compatibility patch was edited.
- **CW:** 39 existing option-label/tooltip localization warnings and namespaced
  static GUI-scope usage warnings remained. No new runtime failure in CW export
  files was identified. This does not prove that every conditional game effect
  or every handoff shape is covered.
- **AVII:** one custom `maritime_trading_handbook` visual failed to select an
  asset for a generated merchant. The loaded event changes native `visuals=book`
  to the custom visual, so this is recorded as an unresolved existing-mod path.
- **SRE:** two war-start callbacks rejected the same invalid casus-belli scope.
  The cause of invalidation was not established; no speculative repair was made.
- **Other diagnostics:** BattleGraphics UV-streaming notices, native activity,
  succession, scheme and artifact contexts remained. The save/load phase also
  produced observer/no-player CFP and Divine Intervention diagnostics, plus an
  older-save POD emblem preview warning. These remain documented rather than
  being treated as a clean-log pass.

This is one fresh campaign with selected native UI, exporter and save/load
checks. It does not certify every event, start date, unrelated mod feature, or
old personal save. Attila stat calibration and actual casualty return are covered
by the separate [battle validation receipt](./stat-fidelity-validation.md).

## Repeatable workflow

1. Preserve personal launcher/settings/save state and use the copied playset.
   Keep raw evidence outside the repository and disable save-on-exit for
   disposable runs.
2. Use the separate debug overlay for probes. Verify current engine API/effect
   documentation, actual getters, modifier presence, and exact restoration.
   New modifier definitions required a fresh process; GUI/event reload was
   verified separately.
3. Freeze all enabled roots and wrappers, then hash their contents before the
   final normal-rendered launch. Remove the debug overlay and use
   `-nofilewatcher` for that run.
4. Close title/county/holding panels **before** entering observer mode. Center
   the map using the native HUD's Go to Capital action while still a player.
   Avoid opening Holding Manager views while observing.
5. Run normal simulation, record native dates/rendering, and monitor uncapped
   log growth. `tick_day`, survival alone, or a capped log are not substitutes.
6. Archive the paused endpoint **before** any Fight handoff: the handoff begins
   with `Log.ClearAll`. Preserve native save/load observations separately.
7. After the test process exits, archive final logs and compare every enabled
   source file and wrapper with the prelaunch hashes. Report unresolved findings
   and skipped cases explicitly.
