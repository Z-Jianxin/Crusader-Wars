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

Technical scope and evidence: [accepted battle/build validation](./stat-fidelity-validation.md), [stat model](./stat-fidelity-model.md), [knight budgets](./knight-budget-calibration.md), [native CK3 test workflow](./ck3-runtime-validation-20261021.md), and [isolated application/replay workflow](../Tests/DevelopmentRuntime/README.md).
