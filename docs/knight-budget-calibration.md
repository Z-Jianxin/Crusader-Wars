# Knight budgets and remaining combat calibration

The old four-body reference preserves relative prowess/efficiency changes, but does **not** preserve knight strength against the mapped army. It is retained only as the explicit `KnightBudgetMode: "TemplateRelative"` test mode. The default `Anchored` mode budgets damage and toughness independently against a verified melee MAA source in the same army, with a native owner-levy fallback for armies without a suitable MAA source.

## What the active database showed

The actual snapshot-6 army has five knights with total prowess 64 and efficiency 2.35. The exported active coefficients are 50 damage and 10 toughness per prowess. Their CK3 totals are therefore **7,520 damage and 1,504 toughness**, before side commander advantage.

The merged 29-pack snapshot, including the eight required Early Medieval 919/Age of Justinian/Mubarizun packs, supplies these relevant records:

| Mapped unit | Normal + AP weapon damage | Supported total HP | Role |
| --- | ---: | ---: | --- |
| `cha_gen_knights` | 6 + 4 = 10 | 150 (rider/bonus 100 + mount 50) | Mounted knights |
| `cha_vik_warlords_companions` | 46 + 4 = 50 | 100 | Huscarl mapping |
| `cha_gen_armoured_sword` | 12 + 2 = 14 | 100 | Armored footmen |
| `cha_gen_archers` | 7 + 2 = 9 melee; 28 + 4 projectile | 100 | Allied bowmen |
| `ANG_geburas_axes` | 8 + 4 = 12, plus a separate infantry bonus | 100 | One levy template |
| `cha_eng_fyrd_axemen` | 12 + 20 = 32 | 100 | Another levy template |

The old reference emitted 20 knight bodies with multiplier 3.008: approximately **600 summed normal/AP weapon damage** after integer rounding. The same army's 400 huscarls emitted 75 weapon damage each, or 30,000 summed weapon damage. Their CK3 damage totals are 7,520 versus 24,000. Thus the knight/huscarl ratio in this nominal weapon sum is approximately **15.7 times too low**. This is a diagnosis of incompatible reference scales, **not** a measured DPS correction factor.

The mapper also gives different levy templates very different weapons and ranged/cavalry roles. A universal levy 10/10 reference cannot be assumed from those templates. CK3 additionally has owner modifiers such as `levy_toughness`. The native effective levy getter was separately proven with modifier application/restoration; fallback anchoring therefore requires the actual exported owner profile and the army's own supported foot-melee levy template.

## Implemented calculation

[KnightBudgetPlanner](../CrusaderWars/stat_fidelity/KnightBudget.cs) receives a native knight roster, an anchor, a mapped representation template, explicit multiplier/body bounds, a side commander multiplier, and the battle representation scale.

1. Sum native damage and toughness separately: `D = sum(prowess * efficiency * damage_per_prowess)`, `T = sum(prowess * efficiency * toughness_per_prowess)`.
2. Select the largest source regiment among same-army, verified, non-siege, foot-melee MAA candidates. Source totals are grouped before selection so splitting does not change the anchor. The active template's weapon/HP fields are adjusted using the MAA's native effective/base ratios and the backend's integer rounding rules. **Commander scaling is excluded at this step.**
3. Compute nominal targets:

   ```text
   target weapon sum = D * anchor_TW_weapon / anchor_CK3_damage
                      * damage_response_factor * commander_multiplier * battle_scale
   target HP sum     = T * anchor_TW_HP / anchor_CK3_toughness
                      * toughness_response_factor * battle_scale
   ```

4. Choose the minimum body count satisfying both independent multiplier ranges, the formation floor, the character count and the maximum body count. No arbitrary anchor-weapon cap is applied. If no body count satisfies the bounds, conversion fails; it does not clamp away part of the budget.
5. Allocate the abstract retinue bodies across existing characters using native toughness weights and largest remainders. No extra CK3 characters are created. Split the retinue into balanced formations no larger than the active template's nominal count (or a smaller explicit limit). Survivor and kill reports are summed across those formations before one character-pool fate pass.

The default knight-specific maximum multiplier 25 is an explicit engineering limit, **not** an empirically validated combat equivalence. Independent response factors default to 1 and are labeled as nominal matching until fitted against battle measurements. The report includes target sums, integer-rounded sums, body allocation, anchor identity, representation scale, support status and calibration evidence.

The early snapshot-6 calculation used a rider-only 100-HP proxy and correctly rejected generation while mounted semantics were unknown. Subsequent native card and combat trials established composite rider + land bonus + mount HP for ordinary one-rider cavalry. Generation now clones/scales those components, preserving the rounded mounted and dismounted totals independently. Special entities remain unsupported.

The fresh framed station fixture uses CK3 huscarl 78/50.76 and generated Attila 98 weapon damage/195 HP. Its five knights total 8,460 damage and 1,692 toughness. The actual mapped horse template has composite 150 HP. Full generation produced **43 bodies**, damage multiplier **24.719141**, HP multiplier **1.007752**, rounded totals **10,621 weapon damage and 6,493 HP** against targets 10,629.230769 and 6,500. The original mounted role was preserved; there was no foot override. The new cache includes the active mount records.

A separately configured foot-template test used `cha_gen_armoured_sword`. It produced 27 bodies, damage multiplier 24.867725 and HP multiplier 2.146739; integer sums were 9,396 and 5,805. Forcing a ten-body formation limit produced three nine-body formations. Real snapshot-6 conversion, all active database records, character-roster validation and the isolated synthetic casualty roundtrip passed. This proves the calculation and integration, not that foot and mounted knight tactics are interchangeable. The override is explicit and is not silently selected for players.

At 50% battle scale both target sums are halved. Character identities remain once, while actual retinue result counts are never unscaled a second time. The knight budget therefore does not become twice as strong relative to the scaled MAA army.

## Required combat measurements

- Ordinary mounted/rider/bonus HP behavior passed native card and combat tests; direct observation of dismount transitions remains useful. A rider-only database sum is never treated as total mounted HP.
- Native engine logs verified requested XML formation counts 20 and 500 against a nominal 160-man template. Continue checking the actual mapped knight deployment during the final game-to-game test.
- Benchmark the chosen representation against its anchor under fixed terrain, fatigue, morale, contact and charge conditions, with mirrored sides and repeated trials. Fit damage and toughness responses separately.
- Inspect overkill and contact limits. Increasing a weapon beyond the target soldier's HP may not increase kills per attack. If response saturates, adjust the body-count floor or multiplier bound; multiplying nominal damage again is not a sound fix.
- Repeat for representative armor/shield and infantry/cavalry opponents, and separate charge impact from sustained melee. Projectile fire, AP share, bonuses against classes, animations, attack/defense, morale and formation width remain Attila mechanics.
- Test prowess and efficiency changes independently, mixed-quality knight pools, a fighting commander counted once, normal/half battle scale, formation splitting, and actual casualty return.

A nominal anchor should not be described as full absolute troop calibration. MAA still use effective/base ratios on their existing mapped templates; CK3 counters, terrain/width formulas, combat rolls, pursuit/screen and independent garrison stats remain separate work. A different anchor can imply a different cross-type scale; the selected anchor and response-factor evidence are always reported.

## Double-counting and roster fixes

The real commander export includes Leading Own Troops +5, so its additional legacy +1 XP is removed when direct commander advantage is applied. Legacy commander/trait XP and Martial stars are also replaced, and Martial-derived defensive obstacles are suppressed in this mode. Direct generals do not add legacy accolade abilities on top of native bonuses. Other condition modifiers are not removed without evidence that the getter already contains them; their legacy XP treatment is explicitly incomplete, including the zero-XP floor for negative penalties.

Unused original commander/individual-knight mapping placeholders are removed before emitting actual formations. A generated character roster must match the emitted XML. Legacy and calibrated `knights_<uniqueID>` reports are recognized; character formation counts are handled in their actual represented scale, and multiple formations receive one aggregated fate/kill pass.
