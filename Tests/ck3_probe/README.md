# CK3 runtime exporter fixture

This is a **test-only mod**. Do not add it to the normal playset or run its events
in a personal campaign. It applies temporary stat modifiers, spawns armies and
saves named fixtures. The production exporter is in
[cc_stat_export.gui](../../ck3_mods/crusader_conflicts/gui/shared/cc_stat_export.gui).

## Baseline, boost and restore

Enable this mod after the production mod and load a disposable save with at least
one player MAA regiment. The existing runtime fixture is Harold of England on
1066-09-15. Click **Run full probe** in the test overlay. The invisible
[probe](gui/cc_stat_probe.gui) then:

1. Records the exact regiment/owner IDs, base stats and native effective stats.
2. Applies +20 MAA damage, +10 toughness, +50 percentage points knight
   effectiveness and +5 martial to that regiment's owner.
3. Records the same getters again, removes the modifier and records restoration.
4. Saves `ccf_probe_boost` and writes a delimited `CCPROBE` frame to the debug log.

Each console action is in its own `on_finish` state. Simultaneous console calls
or a console call triggered reentrantly from another console call are unreliable.
The complete sequence takes about 20 seconds of wall time, even while paused.
The first boost run showed that paused owner modifiers and skills stay cached.
The revised test explicitly calls the engine's documented
`scriptedtests_recalculate_character_modifier` and
`force_character_skill_recalculation` after applying/removing its modifier.
Those test helpers are not used by the production exporter.
Check captured logs with:

```powershell
python Tests/ck3_probe/validate_probe.py '<CK3 user folder>/logs/<prefix>debug.log'
```

A passing result proves owner modifiers reach native getters. It does not prove regiment
stationing, allied ownership, combat selection, or Total War battle results.

The `ccf_boost02` runtime capture passed: damage 40 → 60 → 40, toughness
27.56 → 39.96 → 27.56, knight efficiency 1.85 → 2.35 → 1.85, and commander
advantage 19 → 24 → 19. Base damage/toughness remained 40/26.

The test mod also supplies a visible **CCF disposable test controls** overlay at
the left edge. Its mouse buttons apply/remove the boost and recalculate caches,
read a fresh `CCREAD` frame, save `ccf_runtime_fixture`, run one/ten game days,
raise normal troops, create the battle and add an ally. This avoids reliance on
keyboard injection into the game. The Read stats button creates a fresh native
GUI context and emits regiment/owner IDs, validity flags, base/effective MAA
stats, knight efficiency, commander advantage and active combat constants.

The registered console commands `load ccf_probe_boost` and
`load ccf_probe_boost.ck3` were both recorded in console history but did **not**
load the fixture in the tested GUI context. The misleading reload button was
replaced with **Open pause menu**, using the native `OnPauseMenu` action.
Load through the native Load Game UI, or restart the disposable process using
the verified `-loadsave=ccf_probe_boost.ck3` CLI option. Registration in `help`
is not evidence that a command operates in a particular application phase.
The full probe no longer starts automatically on load, so reloading a combat
fixture does not reset its snapshot serial or remove test modifiers.

**Run 1 day / Run 10 days** use slow real simulation and a reactive date watcher
that pauses inside the game. **Run until ready** similarly pauses as soon as the
selected battle has soft casualties. **Run + handoff** then starts the normal
Crusader Conflicts battle handoff. Select the battle before either battle action.
Both have a 30-day pause limit; **Cancel auto** clears the watcher and pauses.
They log `CCFIXTURE|AUTO_PAUSED|ready`, `date`, or `cancel`.
The readiness watcher reads the combat saved by `ccf_test.1`. `CombatWindow`
is a local native window context; using it from an unrelated debug overlay
compiled but never detected casualties. The production exporter lives inside
the native combat window and retains its actual selected-combat context.

This is necessary because external agent latency can span many game days even
when an unpause/pause pair seems quick. In the recorded battle test, `tick_day`
advanced the displayed date without advancing the selected battle as normal
simulation did; do not use that command as evidence of simulated combat.

## Real combat and the production handoff

Use a disposable save whose player is already at war, with both this test mod and
the production mod enabled. Wait for the boost/restore probe to finish first.

1. Open the debug console and run
   `gui.createwidget gui/cc_fixture_raise.gui cc_fixture_raise`.
   This uses vanilla's `PlayerRaiseAllRegiments`, so the army contains actual
   player MAA and knights.
2. Advance enough game days for the troops to assemble, then pause.
3. Run `event ccf_test.1`. The [test event](events/ccf_test_events.txt) gathers the
   player's armies at their capital, boosts the player, assigns the player as
   commander, and moves an army belonging to a current enemy into that province.
   Check for `CCFIXTURE|combat_created` and inspect the error log.
4. Select that combat. Allow at least one combat tick so the normal Crusader
   Conflicts Fight Battle control enables, then activate that control.
5. Capture the complete `CCSTAT|v1|BEGIN` through `END` frame, the readable handoff
   save, and debug/error logs before another run changes them.

This path tests the real production exporter, including its paused snapshot,
actual selected combat, real owner/regiment/knight IDs and save binding. Do not
substitute hand-written `CCSTAT` records when claiming runtime validation.

The `combat02` runtime run completed the normal Fight Battle flow after fixing
an empty wait animation: snapshot 3 contained two commanders, four MAA profiles
(including a zero-base-damage siege regiment) and five knights, followed by
`BattleType:BATTLE`, `Keyword:CRUSADERCONFLICTS` and the engine-created save.
That save is a CK3-prefixed ZIP whose `gamestate` entry is plaintext. Archive both
the exact save and log from a frame; do not pair a new frame with an earlier save.

The installed engine serializes a combat scope as `type=com`, and omits
`identity` when the combat ID is zero. The recorded save has actual ongoing
combat `combats.combats.0`; its snapshot serial 3 serializes as `identity=300000`.
Use that native representation for save binding, not an invented `type=combat`.

For a separate allied-owner fixture in Harold's defensive war, run
`event ccf_test.2` after the battle exists. It joins one adult vassal to that war
and brings the same MAA types under that unboosted third owner into the combat.
Allow the army to join before capturing another normal production handoff. This
event is not runtime validated until its participant IDs appear in the actual
combat save and exported rows.

The allied regression is now verified. Snapshot 5's save contained defender
armies 18/35/36 and four allied MAA sources, but the participant-based collector
missed those reinforcements before their first contribution tick. Strict replay
correctly rejected the incomplete export. The corrected collector enumerates
armies at the selected combat's location and filters their exact
`involved_combat_side`. Snapshot 6 then captured eight MAA rows across three
owners, and full conversion passed with all four allied source identities.
An isolated casualty round trip preserved different losses for both same-type
allied regiment pairs. This fixture does not exercise allied knights or hired
regiments.

The event's success marker only means the event reached its final stage. Inspect
the save's actual combat, armies, and saved `ccstat_combat` identity before
claiming a successful handoff.

Stationing is verified separately in `station03`. The native military UI
stationed Huscarl source 17620 (owner 32396) in London 1527. The source save uses
`origin=1527` on that source regiment; army chunk origins are not station fields.
Effective damage/toughness changed from 40/27.56 to 52/35.36. Applying a
province-only +30 damage/+15 toughness modifier then gave 91/55.76, and removal
restored exactly 52/35.36. Base 40/26, owner, regiment, efficiency and commander
values remained unchanged. A later real battle with both owner and station
modifiers exported source 17620 at station 1527 with 78/50.76. The corrected
Run+handoff watcher paused at first casualties and completed the normal handoff
without a timed manual click.

The native levy view is verified separately in `levy02`. Its actual
`LevyView.GetLevyType` reports `IsValid=no`: the engine represents levies as a
virtual MAA type. A clean independent context uses
`EmptyScope.GetRootScope.Regiment` followed by `Regiment.GetMAAType`, without
looking up a nonexistent database key. Native and independent reads both gave
base 10/10 and owner damage 10, with owner toughness **11.1 → 5.55 → 11.1** after
applying/removing a flat levy-toughness -5 modifier. The MAA control remained
52/35.52 and all values restored exactly. The native UI also showed 11 → 5 → 11.
`validate_levy_probe.py` checks all three states, owner identity, unchanged MAA
control and actual modifier application/removal markers. New modifier
definitions require a fresh game process.

Production `LEVY` rows are keyed by exact combat side and army owner. They use
the same directly collected army roster as MAA profiles, deduplicating owners
without losing pure-levy armies. After Add allied army, the optional Add levy
army button adds a separate 750-man army with no MAA under that ally; use its
actual saved army identity to test conversion coverage and owner binding.

That production check passed in `levy02` snapshot 4. Three deduplicated owner
rows carried native damage/toughness 10/13.44 (William), 10/9.6 (ally) and
10/11.1 (Harold), each with native base 10/10. Full conversion with the active
Attila database produced 26 profiles for one attacker and three defender armies;
synthetic casualty serialization passed. Saved army 30 was the separate 750-man
pure-levy army with no commander. Its generic Total War general remained
untracked, so no death/capture could be assigned to the army owner.

Legacy header guards also explicitly represent an absent side commander as
ID 4294967295, name `Uncommanded army`, and culture/rank/prowess 0; an actual
unlanded commander receives rank 0. These protected GUI branches were validated
against engine API types and source structure after the snapshot 4 capture, but
the captured combat's two *side commanders* were present. A commanderless
supporting army is not proof of an absent side-commander branch.

## Debugging practices

- Preserve the current `dlc_load.json`, settings and existing exit/autosaves
  before switching to test mods. Disable save-on-exit for disposable runs and
  restore the original settings afterward.
- Use a unique log prefix for each run; preserve the command line and the
  corresponding logs. A startup smoke test is not a simulation soak test: allow
  a longer running campaign to collect delayed events and errors as well.
- Native engine documentation is authoritative for the installed build. The
  verified `-run_console_action=dump_data_types` and
  `-run_console_action=script_docs` startup commands emit GUI and script API
  references into the game's user log directory.
- GUI files can hot reload in debug mode. After edits, inspect the error log and
  recreate the fixture widget; don't assume an already completed animation
  restarts. Use a fresh process when changing the active mod list or when unsure
  whether a script database reloaded.
- Repeated `gui.createwidget` calls can leave several disposable probe instances.
  A broad GUI hot reload can restart their on-create/triggered animations
  together and produce console-reentrancy errors. Archive finished captures
  before hot reload and use a fresh process for the final frozen run. The
  engine help lists `gui.clearwidgets`, but its cleanup behavior is not yet part
  of the verified fixture workflow.
- A GUI animation state containing only `duration` and `next` is rejected as
  having no valid properties. The handoff wait stage must explicitly trigger
  its next stage in `on_finish`; otherwise the numeric frame completes but the
  later battle marker and save never happen.
- `every_side_participant` is insufficient for a complete army roster: fresh
  reinforcements may not yet have contribution records. Use the selected combat
  location's `every_army_in_location` and require exact side membership. Keep a
  negative regression fixture proving that conversion rejects missing profiles.
- Native `MenAtArmsType.GetStat(Character.Self, Province.Self, Title.Self, stat)`
  works when the optional province/title are invalid. Passing the equivalent
  nested optional getters directly can suppress the entire expression.
- `GetDefine('NCombat', 'KNIGHT_DAMAGE_PER_PROWESS')` returns `int32`; convert with
  `IntToFixedPoint` before the machine decimal formatter. Numeric script value 1
  serializes in this build as `type=value identity=100000`.
- A failed probe should fail visibly. Preserve incomplete frames and error logs;
  don't manufacture missing values or silently reuse an older successful frame.
- Legacy handoff headers can also be incomplete: a real capture omitted
  `PlayerCharacter_ID` entirely when a localization expression read a just-set
  variable. The handoff now pauses first, uses direct native fields, and runs one
  shared controller instead of four duplicate button pipelines. Every current
  legacy header and the numeric snapshot are enclosed in
  `CCHANDOFF|v1|BEGIN` / `CCHANDOFF|v1|END`; the final notification comes afterward.
  A reader must reject a latest incomplete frame rather than borrow older IDs,
  terrain, winter, or names from the persistent console history.

The environment and command syntax above were checked against CK3 1.20.1.2,
Steam build 26046173. Revalidate after game updates.
