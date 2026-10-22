# Future plan for automatic battle return

**Status: proposed, not started.** This document is a plan only. It does not enable automatic loading or authorize code, runtime, campaign, configuration or process changes.

## Outcome and current boundary

The proposed feature would take an accepted Attila result, publish a dedicated CK3 save, return CK3 to that exact save through its normal supported loading path, and confirm native battle completion before accepting another battle. The player would no longer need to copy the result or locate it in Load Game. A failed automatic return would leave the same clear manual path available.

The separate [combat-mechanics fidelity plan](./combat-mechanics-fidelity-plan.md) covers the remaining translation and calibration work. Automatic return is a workflow/reliability project and should not silently change those combat rules.

The [accepted v4 return](./stat-fidelity-validation.md) is the foundation: one actual AAR was imported with its original fate decisions, loaded and resaved natively, and advanced through battle completion. CK3 awarded war score once and populated report accumulators once. This proves the tested save/return semantics; it does **not** prove an unattended loader, a native hot-load API, or every campaign/version combination.

Current relevant behavior:

- [SaveFile](../CrusaderWars/data/save_file/SaveFile.cs) publishes a separate result directory containing the save, input/export/AAR evidence, fate ledger, statistics and hashes when the development marker is present.
- [MainFile](../CrusaderWars/client/MainFile.cs) retains that evidence and stops the automatic listener after a successful battle. Its guarded result-loading path intentionally asks for an explicit manual load. The legacy path outside that guard depends on `--continuelastsave` and personal save/Continue updates; it is not the proposed baseline.
- [DevelopmentRuntime](../CrusaderWars/client/DevelopmentRuntime.cs) also guards upstream replacement and CK3 component copying, and the process wrapper skips CK3 OS suspension because suspension blocked native Attila input during testing.
- [The current usage guide](./using-ck3-stat-fidelity.md) remains authoritative until this proposal is implemented and validated.

Add automatic return as an independent, initially opt-in capability. Do not remove the development marker wholesale to reactivate legacy automation. Preserve update/copy guards, responsive in-game pause, separate result publication and explicit saved preferences.

## Required guarantees

1. **Keep the accepted result immutable.** Retain the validated input, complete framed export, deployed formation mapping, actual AAR, committed character-fate decisions and final result. Loading uses a verified copy; retries never overwrite or rebuild the accepted bundle unnecessarily.
2. **Bind the whole transaction.** A snapshot serial or filename alone is insufficient. Record a unique transaction ID, input/result/AAR hashes, campaign identity where available, player, combat/result IDs, native date, source/deployment identities and attempt history. Deliberately replaying a battle is a separate explicit attempt, not an automatic duplicate import.
3. **Leave personal save routing alone by default.** Publish to a unique transaction-specific name in the configured CK3 save folder. Do not replace personal `last_save`, a generic `battle_results` file, or Continue metadata as the transport mechanism. CK3 may itself update metadata or autosaves during normal play; that engine behavior must be tested and distinguished from companion writes, not promised away.
4. **Apply effects once.** Reuse committed fate decisions after a crash or retry. Preserve prebattle war state; CK3 owns its one completion award. Keep pending `final_count` accumulators at zero for native completion. Never feed an already completed return back through casualty conversion.
5. **Require positive load and completion evidence.** A successful process start, rendered map, matching date or elapsed timeout is not sufficient. The acknowledgement must identify this transaction and campaign. Completion verification must distinguish legitimate native travel/regrouping from troop or fate corruption.
6. **Fail without publishing a partial return.** Missing/malformed reports, changed identities, incompatible state or an ambiguous recovery leave the listener stopped and the accepted evidence available.

## Reliable baseline: dedicated save, supported restart/load

Use a targeted normal CK3 load/relaunch path as the first implementation. Native test sessions have exercised a named `-loadsave` launch. Production/non-debug use and the already-running menu path still need explicit validation; no in-place hot-load command is proven. The production wrapper must prove the exact argument and acknowledgement behavior on supported game builds rather than assuming a universal command contract. Do not use the mutable personal Continue target to select the return save.

The proposed durable state sequence is:

```text
Result validated → Bundle published → Load requested
    → Loaded and verified → Native completion verified → Listener ready
                              Any uncertainty → Held for recovery/manual load
```

**Before loading**, compare the transaction with the current campaign and environment. Check the configured executable/build, CK3 component/protocol, enabled playset order and relevant content fingerprints, DLC selection, selected mapper/submods/active pack stack, and conversion settings. Confirm that the current CK3 session still belongs to the paused handoff or is safely at its menu. A newer campaign/save, another player session, changed mods or game update must not cause an automatic rewind or speculative migration. Harmless differences may later receive an explicit compatibility policy; the first version should hold the return when material identity cannot be established.

**Publish atomically**, outside the game's save catalog first. Validate metadata byte length, archive integrity, exact source/fate/report invariants and the manifest before committing the result bundle. Stage a verified copy outside the scanned catalog on the destination volume, then use a proven atomic rename into the unique final save name. Commit a durable publication record only after that succeeds. Partial files must not appear as selectable saves. The failed v1 load established that a valid ZIP can still have an invalid native metadata wrapper. Validate filesystem and cloud-redirection behavior instead of assuming every storage location provides the same atomicity. Cross-volume copies require separate verified staging at the destination.

**Load through ordinary game behavior.** Prefer a graceful, targeted close/relaunch of the exact known handoff process when restart is permitted. Identify the process by executable and start identity, not merely by every process called `ck3`. Honor process-closure preferences; if restart is disallowed or graceful exit times out, offer manual loading instead of force-killing unrelated or uncertain sessions. If CK3 is already closed, launch the named target directly. An already-running different campaign is a hold condition.

**Verify the load, then completion.** Research a minimal correlated acknowledgement using supported CK3 scripting/export facilities, proven in normal gameplay mode. It should establish the expected transaction, player/campaign and selected combat state. Where an ordinary native resave is available, compare the saved state as the stronger check. Agent-driven desktop automation used during development is a test instrument, not a capability automatically bundled with the companion. If a dependable production acknowledgement cannot be established, retain manual confirmation as a clearly identified fallback and do not advertise fully unattended completion.

The first version should preserve the game's paused return and observe completion when the player resumes normally. A separate future preference could permit a bounded native completion tick followed by pause, after its side effects are tested; never run the campaign indefinitely merely to obtain an acknowledgement. The accepted fixture completed after one real game day, but that is evidence for that fixture, not a universal timeout or amount of time to force forward.

**Rearm only after verification.** Record that native completion consumed this transaction before starting the next listener. Archive the prior frame/AAR first. Delayed notifications and repeated report-dismissal events must become no-ops for a completed transaction. A failed acknowledgement must not trigger another casualty import or erase evidence through an automatic fresh Execute cycle.

## Crash recovery and one-time effects

Use a small durable transaction journal with explicit state transitions and content hashes. Writes need a temporary file, flush and atomic replacement appropriate to the target filesystem. Recovery must inspect durable state and current native evidence rather than trust an in-memory boolean.

Persist the complete fate plan before publishing a result. The implementation must explicitly address a crash during decision generation—for example, persist the deterministic decision inputs before evaluation, or hold incomplete decisions for recovery. It must never reroll a previously committed outcome. Code/game version changes must not silently reinterpret such a plan.

Publication and load requests may be retried, but their effects must be idempotent: reuse the same verified named save, detect an already loaded/completed transaction, and avoid duplicate launches. If CK3 completed the battle but the app crashed before recording its acknowledgement, reconcile the native completion evidence and advance the journal; do not reload the pre-completion result and rewind subsequent play. If that reconciliation is ambiguous, manual recovery is the safe boundary.

| Failure | Proposed behavior |
|---|---|
| Incomplete AAR or mismatched source/deployment identity | Stop before result publication; retain diagnostic evidence. |
| Copy interruption, disk-full condition or file lock | Keep staging outside the catalog or under a non-save temporary name; retry only the incomplete publication step. |
| Existing destination with a different hash | Keep both inputs intact; choose a new transaction-specific destination or hold for review. |
| Game/playset/campaign changed since handoff | Hold automatic loading with a concrete mismatch explanation. |
| Load fails, crashes or times out | Preserve the validated bundle, do not overwrite personal routing files, and expose the manual path. |
| Load succeeded but completion is unconfirmed | Keep the listener stopped; continue observing or require explicit manual recovery. |
| Completion happened before an app crash | Reconcile correlated native evidence; never apply the AAR or war award again. |
| User cancels automatic return | Leave the named save and evidence available; do not silently re-enable automation next launch. |

Manual fallback should show the exact published save, an **Open result folder** action and ordinary CK3 Load Game instructions. It must clearly distinguish “result published,” “loaded,” and “native battle complete.” The fallback should reuse the accepted bundle and record any verified manual completion; it should not require rebuilding the battle or rerolling characters.

If the player explicitly leaves automatic mode and resumes the existing manual workflow, record that as a manual override, not a verified automatic completion. It must not silently bypass the automatic mode's acknowledgement gate or replay an already accepted AAR.

## Delivery phases and acceptance tests

| Phase | Work and acceptance boundary | Relative effort |
|---|---|---|
| 1. Return identity and journal | Define transaction binding, committed fate reuse, state transitions and recovery without launching games. Test duplicate events and interruption at every commit boundary. | Medium |
| 2. Atomic dedicated publication | Preserve the accepted bundle, publish uniquely, validate the native prefix and complete content, and keep partial files outside the catalog. Test locks, disk-full/copy interruption, existing names and hash mismatch. | Medium |
| 3. Supported loader and acknowledgement | Prove targeted normal launch/load, safe running-session handling, and a correlated production acknowledgement. Retain manual fallback if the acknowledgement cannot be proven. | Medium–large; main uncertainty |
| 4. Native completion and rearming | Preserve one-time fates/war/report semantics, verify completion and only then rearm. Test app/game crashes before and after every acknowledgement. | Large validation effort |
| 5. Opt-in rollout | Explain restart/pause behavior, preserve user opt-outs, expose recovery status, and validate through the packaged application's UI. | Small–medium |

Use disposable campaigns and the [existing native runtime procedure](../Tests/DevelopmentRuntime/README.md). Reuse the [accepted v4 evidence](./stat-fidelity-validation.json) as a regression oracle; automated import tests alone cannot prove a native load. The test matrix should include normal Fight and Continue Battle, single/multi-owner armies, no commander, zero survivors, wounded/captured knights, save-folder redirection and Unicode names, an already running unrelated campaign, changed playset/version, and repeated restart/recovery. Verify the entire native return through completion, including a single war entry and non-doubled report counts, while allowing documented native army regrouping and living-knight travel.

The existing [offline replay harness](../Tests/StatFidelity.BattleHarness/README.md) can support deterministic serialization and failure fixtures. New tests should also prove that personal routing files are not used by the companion, previous immutable evidence survives, and a listener cannot consume the same transaction twice. Native tests must separately observe CK3's own autosave/exit behavior and protect unrelated saves during the test setup.

The baseline is a moderate implementation project with a larger recovery/native-validation burden. Existing publication and validated return code reduce the starting work; a trustworthy production load/completion acknowledgement is the first feasibility milestone. A calendar estimate should follow that milestone, rather than treat launching a process as the whole feature.

## Optional native hot-load research

After the restart/load baseline works, investigate whether CK3 exposes a normal supported in-process load command or menu flow that can select the dedicated save and provide equally strong acknowledgement. Test this separately across supported builds, localization and game states. A menu path may reduce relaunch time but is still a load, not an in-memory battle merge.

Research must not assume that debug console commands, development GUI shortcuts or agent UI tools are suitable production APIs. Do not use live-memory injection, undocumented structure patching or an unverified mechanism to splice casualties into a running campaign. Do not weaken stale-state, one-time-effect or completion checks to make a faster path appear to work.

This research has high uncertainty and is not a dependency of the first release. If no stable supported hot-load path is found, the normal restart/load baseline and manual fallback remain the intended design.
