# Future plan for named knight retinues

Status: future proposal, not started. Saved from the 2026-10-21 discussion. This records a follow-up feature; it does not change the installed build or authorize implementation now.

## Goal

Make CK3 knights recognizable while commanding their formations in Total War: Attila. The preferred direction is one separately selectable retinue per knight, with a name such as **Gyrth Godwinson's Retinue**, that knight's combat contribution, and character-specific casualty attribution.

## Scope and alternatives

| Approach | Player experience | Estimated relative scope |
|---|---|---|
| Reliable pooled names | A shared unit lists its knights, such as “Knights: Gyrth, Godwine, Morcar…” | Small follow-up |
| Individual named retinues | Each knight's retinue has its own unit card and can receive separate orders | Moderate follow-up; preferred direction |

A retinue represents a knight and supporting fighters. It does not identify one particular visible soldier as that character. CK3 portraits, matching faces, custom hero models, individual animations, and direct tracking of a specific soldier's death are outside this plan.

More individual retinues mean more unit cards and more micromanagement. Keep the presentation and any optional pooled mode explicit. Do not assume the existing Combine Knights option already enables individual retinues in fidelity mode.

## Existing foundation

- Native CK3 character IDs, names, prowess, knight efficiency, and per-character body allocations are available to the converter. Some names still rely on legacy parsing or a fallback, so reliable display-name export needs review.
- [Unit naming](../CrusaderWars/locs/UnitsCardsNames.cs) already supports pooled knight lists and named units. Shared-template naming can suppress or misassign labels when multiple armies use the same template.
- [Generated variants](../CrusaderWars/packfile/stat_fidelity/StatVariantBuilder.cs) already have private unit keys and localization rows. Give each intended formation its own display text rather than relying on a shared base-template name.
- [Current knight generation](../CrusaderWars/twbattle/UnitsFile.cs) pools knights into formations. [Knight budgets](./knight-budget-calibration.md), exact deployed-roster binding, and one-time character-fate handling provide reusable infrastructure.

## Proposed implementation sequence

1. **Reliable identities and labels.** Carry the CK3 display name alongside the stable character ID through export, conversion, generated localization, and the battle report. IDs determine identity; names remain display text. Handle duplicate names, titles, long names, accents, and XML/localization escaping. Provide a clear fallback when a display name is unavailable.
2. **Individual formations.** Replace pooled generation in the new mode with one retinue per knight. Derive each retinue's stats and size from that character's prowess and effective knight efficiency. Preserve the combined nominal damage/toughness budgets within documented rounding tolerances and existing safety bounds. If one retinue needs multiple formations, preserve their shared character identity. A fighting commander must not receive a duplicate knight budget.
3. **Individual result attribution.** Bind every retinue formation to its character ID. Aggregate its reports once, use its own losses in that character's fate calculation, and attribute its formation kill credits without dividing a pooled total among unrelated knights. Retinue kills are formation credits, not measured personal kills. A wiped-out formation must not automatically mean the character died; preserve wounded and captured outcomes.
4. **Readable language support.** Test names in the actual Attila UI. Prior English Attila tests showed missing Chinese glyphs despite preserved Unicode text. Investigate compatible font/localization support. Treat a Latin-name fallback as an explicit presentation choice, not an assumed equivalent to the CK3 display name.

## Acceptance checks

- Each selectable retinue shows the correct knight and receives independent movement/attack orders. Allies and enemies using identical templates or identical names remain distinguishable.
- Combined budgets remain consistent with the existing model, with no duplicate commander/knight contribution or unintended extra strength from splitting formations.
- Exact formation-to-character binding survives regeneration and battle return. Every casualty/fate is applied once, and unrelated characters and source regiments remain unchanged.
- Native UI checks cover long names, multiple languages, Chinese glyph rendering, many knights, and practical unit-card crowding. Engine formation limits must be measured rather than assumed.
- A real battle return passes fresh CK3 loading and native completion, including knight outcomes, source counts, report accumulators, and a single war-score award. Preserve the guarantees in the [accepted validation receipt](./stat-fidelity-validation.md).

Use the existing [development runtime workflow](../Tests/DevelopmentRuntime/README.md) and [CK3 debug overlay](../Tests/ck3_probe/README.md). Keep test controllers and private saves/logs outside the delivered build and tracked source. Reassess the scope after an initial native naming/retinue prototype; no delivery date is committed.
