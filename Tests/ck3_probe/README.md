# CK3 runtime exporter fixture

This is a **test-only mod**. Do not add it to the normal playset or run its events
in a personal campaign. It applies temporary stat modifiers, spawns armies and
saves named fixtures. The production exporter is in
[cc_stat_export.gui](../../ck3_mods/crusader_conflicts/gui/shared/cc_stat_export.gui).

## Baseline, boost and restore

Enable this mod after the production mod and load a disposable save with at least
one player MAA regiment. The existing runtime fixture is Harold of England on
1066-09-15. The invisible [probe](gui/cc_stat_probe.gui) automatically:

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

This proves owner modifiers reach native getters. It does not prove regiment
stationing, allied ownership, combat selection, or Total War battle results.

The test mod also supplies a visible **CCF disposable test controls** overlay at
the left edge. Its mouse buttons apply/remove the boost and recalculate caches,
read a fresh `CCREAD` frame, save `ccf_runtime_fixture`, advance one/ten game days,
raise normal troops, create the battle and add an ally. This avoids reliance on
keyboard injection into the game. The Read stats button creates a fresh native
GUI context and emits regiment/owner IDs, validity flags, base/effective MAA
stats, knight efficiency, commander advantage and active combat constants.

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

For a separate allied-owner fixture in Harold's defensive war, run
`event ccf_test.2` after the battle exists. It joins one adult vassal to that war
and brings the same MAA types under that unboosted third owner into the combat.
Allow the army to join before capturing another normal production handoff. This
event is not runtime validated until its participant IDs appear in the actual
combat save and exported rows.

The event's success marker only means the event reached its final stage. Inspect
the save's actual combat, armies, and saved `ccstat_combat` identity before
claiming a successful handoff. Stationing coverage still needs a separate fixture
with real stationed regiments and independently verified per-province bonuses.

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
- Native `MenAtArmsType.GetStat(Character.Self, Province.Self, Title.Self, stat)`
  works when the optional province/title are invalid. Passing the equivalent
  nested optional getters directly can suppress the entire expression.
- `GetDefine('NCombat', 'KNIGHT_DAMAGE_PER_PROWESS')` returns `int32`; convert with
  `IntToFixedPoint` before the machine decimal formatter. Numeric script value 1
  serializes in this build as `type=value identity=100000`.
- A failed probe should fail visibly. Preserve incomplete frames and error logs;
  don't manufacture missing values or silently reuse an older successful frame.

The environment and command syntax above were checked against CK3 1.20.1.2,
Steam build 26046173. Revalidate after game updates.
