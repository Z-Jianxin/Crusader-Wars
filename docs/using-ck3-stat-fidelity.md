# Using the CK3 combat-stat build

The standalone Windows application is installed separately at:

`C:/[GAME]Crusader Wars/Crusader Conflicts - CK3 Stat Fidelity`

Open `CrusaderConflicts.exe` in that folder. The .NET runtime is included; no terminal or environment-variable setup is required. Automatic upstream updates are disabled to preserve the fork.

1. In the CK3 launcher, select **vanilla-20261013-crusader-conflicts**. Start your campaign with that copied playset.
2. Open the separate Crusader Conflicts application and press **Execute** before using CK3's normal **Fight the battle yourself** button. **CK3 combat stats (experimental)** is already enabled in Options.
3. Play the generated Total War: Attila battle normally. This installed build contains no automated battle controller.
4. Dismiss Attila's result and close the converter's after-action report. The converter stops and announces that a separate result save is ready.
5. Open the newest folder under the application's `results` directory. Copy `battle_results.ck3` into your CK3 `save games` folder using a distinct name, then return to CK3's main menu and choose **Load Game** for that file.

The CK3 save folder is normally:

`%USERPROFILE%/Documents/Paradox Interactive/Crusader Kings III/save games`

Use your actual Documents folder if Windows redirects it to OneDrive or another location. The installed copy of this guide can include the local path.

The application leaves your personal last-save and Continue target alone. Each result folder retains the native input, battle export, Attila result and resolved character-fate record for inspection. A later battle begins only when you press Execute again.

After loading the returned save, resume CK3 so it can finish the imported battle. CK3 calculates the battle's war score and adds its history entry at that point; the converter's report marks that award as pending. Its native troop totals exclude Attila's extra general guards and convert knight retinues back into individual CK3 characters.

For an interrupted attempt, **Continue Battle** can regenerate the battle from its saved handoff. Choose the restart option when Attila is already running so the exact deployed roster is reconstructed.

The current translation covers effective men-at-arms and owner-levy damage/toughness, character commander advantage, and independent knight damage/toughness budgets from prowess and knight efficiency. Attila's simulation determines combat results. CK3 counters, combat width/terrain modifiers, combat rolls, pursuit/screen values, siege progress and independent garrison stats are not fully translated. The mapping is therefore labelled experimental.

Future proposals cover [counters, terrain and pursuit/screen](./combat-mechanics-fidelity-plan.md) and [automatic result loading](./automatic-battle-return-plan.md). Both are drafts; the manual loading instructions above remain the current behavior.

Technical scope and evidence: [accepted battle/build validation](./stat-fidelity-validation.md), [stat model](./stat-fidelity-model.md), [knight budgets](./knight-budget-calibration.md), [native CK3 test workflow](./ck3-runtime-validation-20261021.md), and [isolated application/replay workflow](../Tests/DevelopmentRuntime/README.md).

## Default Attila unit packs

For **Default CK3**, a previously unconfigured companion now selects **Dahan China** automatically. The **Optional Sub-Mods** dialog can disable it; an explicit empty selection remains saved across restarts and switching playthroughs.

**Earl's Realistic Units** already consists of three mandatory packs in the High, Late, and Renaissance mapper manifests. All three load automatically for the applicable era, beginning at **1130 AD**. Early Medieval ends at 1129 so the 919 and 1212 stacks are not combined at the boundary. Install all three Workshop items: [infantry](https://steamcommunity.com/sharedfiles/filedetails/?id=3148909626), [missiles](https://steamcommunity.com/workshop/filedetails/?id=3065896492), and [cavalry](https://steamcommunity.com/sharedfiles/filedetails/?id=3047562159).

Dahan requires its [main mod](https://steamcommunity.com/workshop/filedetails/?id=2826656101), [English patch](https://steamcommunity.com/sharedfiles/filedetails/?id=1559011232), and the [Crusader Conflicts compatibility pack on Nexus mod 25](https://www.nexusmods.com/totalwarattila/mods/25?tab=files). The current Early Medieval mapper loads the original main pack and English patch; the later-era mappers load the compatibility pack in place of the original main pack, together with the English patch. The Nexus page titled AD919 (mod 24) is a different dependency.

The general Mod Manager's **Active** checkboxes represent manually selected extras. Required packs and enabled submods are selected separately for the battle's era, so unchecked rows do not mean those automatic packs are disabled. A missing applicable pack now stops launch before the existing launch list is replaced. This checks presence; the separate mod verifier reports differences from the mapper's pinned file hashes. Automatic selection does not establish compatibility of an updated Workshop version.

In CK3 combat-stat mode, the [siege compatibility update](./siege-compatibility-validation.md) checks artillery against the effective loaded database, preserves individual source regiments, and records crew bodies separately from CK3 engine counts. Partial batteries receive private records with matching crew and engine counts. A targeted automatic mapper correction addresses the inherited Early Medieval Imazigen onager infantry proxy; valid active-pack artillery definitions and explicit manual replacements remain protected. Unsupported siege templates stop conversion with a diagnostic rather than silently dropping their source regiments.

Generate a fresh battle after installing that update. Results from an earlier generated roster must not be imported against a newly generated roster. Very high stats can also be legitimate CK3 mod effects: the captured test's active difficulty and skill modifiers produced Martial 100 and a fivefold commander damage factor. The converter retains the native exported values.
