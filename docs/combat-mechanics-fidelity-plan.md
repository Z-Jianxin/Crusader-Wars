# Future plan for counters terrain and pursuit fidelity

Status: future proposal, not started. Drafted from the 2026-10-21 discussion. No implementation, engine experiments, settings changes, or game tests are authorized by this plan alone.

## Goal and current baseline

Extend the existing direct-stat conversion so CK3 counters, battle terrain and conditional advantages, combat width, pursuit, and screen have explicit, measured effects in Attila. Preserve tactical control and truthful casualty return. Matching every battle outcome across two different combat engines is not an acceptance criterion.

The [current model](./stat-fidelity-model.md) already translates effective MAA/owner-levy damage and toughness, character commander advantage, and knight budgets. The existing battlefield generator also represents terrain, crossings, and weather in some form. That does not establish equivalent numerical combat modifiers. Pursuit and screen are exported as evidence but do not currently drive Attila chase behavior. The accepted battle had no additional pursuit-phase loss delta, so it cannot validate a new pursuit model.

## Shared prerequisite and rules

Before changing a mechanic, establish its current CK3 rules from installed definitions and engine-generated interfaces, then verify the relevant getters in disposable native fixtures. Prefer engine-resolved values; label any necessary reconstruction or approximation. Do not hardcode remembered formulas or assume an “effective” getter already includes every combat condition.

Extend the versioned, framed handoff with the exact battle context and source identities needed by each mechanic. Distinguish a verified zero from unavailable evidence. Keep every value bound to the same paused battle/save; missing or stale required inputs must not borrow an older snapshot.

For each applied factor, record its origin and which layer owns it: owner/station bonuses, character advantage, counter effects, environmental modifiers, or native Attila behavior. Apply each contribution once. Replace overlapping legacy XP approximations only where the direct implementation is verified. All generated database records remain private to the battle; original game packs stay untouched.

## Workstream 1 MAA counters

**First deliverable:** represent CK3's verified counter effect at handoff as a documented modifier on the affected source regiments. This is a snapshot of CK3's composition-based counter state, not a claim that Attila uses the same target-selection rules.

1. Establish native counter relationships, strength ratios, resistance/efficiency modifiers, and applicable limits for the active CK3 version and mods. Determine whether the engine exposes the resolved penalty per regiment or type; only reconstruct it if the required inputs and formula can be validated.
2. Include every participating allied, hired, and split source once. Use native troop identities/counts rather than Attila guard or retinue body counts. Keep type-level counter results and per-source stat profiles distinct.
3. Apply the counter contribution through the existing private stat variants without multiplying a penalty already present in exported damage. Record the baseline, counter factor, and resulting target for inspection.
4. Measure interaction with the mapped Attila template's existing anti-type bonuses, armour and attack/defence behavior. Do not assume another anti-cavalry bonus is needed just because CK3 counters cavalry. If private template adjustments are required to reach the intended relationship, document the tradeoff.
5. Treat live recalculation as a separate research gate. First prove that Attila can sample the relevant remaining strengths and apply/revoke a scoped effect through supported APIs. Do not assume Warhammer scripting methods or database hot reload work in Attila. If this is infeasible, retain a clearly labeled initial-counter mode.

**Acceptance:** no-counter and zero-strength controls; one counter and multiple counters; resistance/modded limits; allied duplicate types; split/merged source identities; both sides and mirrored deployment. Exact exported/applied factors must match the verified CK3 fixture. Repeated native battles must show the intended direction and reasonable response under controlled conditions; a particular winner is not guaranteed.

**Relative scope:** medium to large for verified initial counters; dynamic counters are a separate, higher-uncertainty feature.

## Workstream 2 Terrain width and conditional advantage

**First deliverable:** a documented separation of the CK3 battle conditions already reflected in the selected Attila map from numerical modifiers that remain missing, followed by calibrated support for a small set of common conditions.

1. Export or derive verified battle terrain, attacker/defender roles, crossing type/direction, relevant weather/winter, and conditional combat effects. Use the battle location, not a regiment's stationing province. Separate deterministic conditions from CK3 combat rolls and distinguish character advantage from whole-combat advantage.
2. Build a coverage table for hills/mountains, forests, plains, wetlands, rivers/straits and other supported cases. For each, identify effects already present through map geometry, weather, fatigue, movement, template bonuses or legacy XP, then identify the residual CK3 contribution. Map appearance alone is not evidence of numerical equivalence.
3. Add only verified residual damage, toughness or advantage effects through private profiles. Leave random CK3 combat rolls out of the initial deterministic mapping rather than freezing one favorable roll into the entire Attila battle. Conditional faith, supply or commander-trait effects require their own proven inputs and ownership checks.
4. Research combat width separately. CK3's number of troops fighting simultaneously is not the same as Attila formation frontage or total army size. Compare deployment/frontage constraints and controlled reinforcement approaches before choosing one. Preserve total troop strength and casualty identities; do not delete excess soldiers to simulate width.
5. Start with a small validated set, such as an open-field control, a constrained terrain case and a river crossing. Expand the supported matrix after measurements establish which layer supplies each effect.

**Acceptance:** same forces with one condition changed; attacker/defender swap; crossing and non-crossing controls; stationed-stat controls; multiple simultaneous conditions; dynamic width/reinforcement cases if implemented. Confirm the right map, source factors, actual native behavior and absence of duplicated XP/stat bonuses. Test representative 919 and 1212/Earl/Dahan mappings rather than assuming one mapper proves all of them.

**Relative scope:** large and staged; combat width has a separate design/prototype decision because it can change player control.

## Workstream 3 Pursuit and screen

**Preferred first direction:** let Attila resolve visible retreat and pursuit, while calibrated, supported effects make CK3 pursuit and screen influence that phase. A global movement or health bonus must not be presented as faithful pursuit/screen support if it also changes the main battle in an uncontrolled way.

1. Verify effective pursuit/screen inputs and CK3 phase rules. Establish a real main-battle/retreat boundary in Attila from observable native events and survivor snapshots; two AAR reports alone do not establish two distinct gameplay phases. A callback counter is not a verified simulation clock or phase boundary.
2. Prove available phase-scoped controls before selecting a mapping. Candidates include chase lethality or withdrawal survival; speed, fatigue, morale and abilities need independent tests for side effects. Do not promise an unverified runtime stat API.
3. Keep actual main and post-rout survivor/kill reports bound to the exact deployed formations. Treat withdrawal, routing, leaving the map and death as distinct states where native evidence permits. Apply witnessed losses once, including split formations and named retinues when that separate feature exists.
4. Preserve one character-fate resolution and the accepted native completion contract: no extra repeated CK3 casualty debt, no duplicate war award, and no prefilled native report accumulators.
5. If Attila cannot express the required phase effects, evaluate a separate **CK3 aftermath mode** as a product decision. Prefer allowing native CK3 pursuit to run once from a verified main-battle endpoint if that handoff can be proven; do not presume Attila can be stopped at the required boundary. A reconstructed aftermath would need its own validated rules. Either alternative needs an explicit way to avoid counting already observed Attila pursuit losses again, bounded survivors, and transparent reporting of off-screen losses. Do not silently add losses to the accepted AAR or enable an aftermath alternative by default. The choice between tactical pursuit and CK3-resolved aftermath must be settled before implementing that alternative.

**Acceptance:** a real fixture with nonzero pursuit losses; zero-pursuit and strong-screen controls; both winning sides; infantry/cavalry combinations; voluntary withdrawal versus rout; units leaving the map; depleted forces; wounded/captured knights; repeat import/recovery. All source totals and phases must reconcile without inventing per-character kills or increasing survivors. Test pause and fast-forward separately and record verified phase events plus wall time.

**Relative scope:** medium to large for phase extraction and calibrated behavior; final effort depends on supported Attila controls and the aftermath decision.

## Delivery sequence and release criteria

1. Establish the shared modifier-coverage ledger, export contract and controlled baseline fixtures.
2. Deliver initial counter support and a limited terrain/advantage set independently, with neutral controls and explicit coverage reports.
3. Prototype width and pursuit behavior behind separate options. Keep unresolved mechanics labeled as unsupported or approximate.
4. Combine supported mechanics and test their interactions, including the [planned named retinues](./named-knight-retinues-plan.md). Use repeated, mirrored native battles and predeclared measures/tolerances; one battle or a serialized pack is insufficient calibration.
5. Complete exact AAR/source/fate audits and a fresh native CK3 load, save and completion check. Retain the single-award/accumulator guarantees from the [accepted return validation](./stat-fidelity-validation.md). If CK3 exporter/scripts change, also apply the repository's frozen-source long-run validation workflow.

Remove experimental labels only for the specific mechanic and configurations that satisfy their acceptance checks. Siege/garrison fidelity, special-entity HP and full cross-engine balance remain separate scope. No delivery date or unproven engine capability is committed.

The [automatic battle-return plan](./automatic-battle-return-plan.md) can proceed independently: it should automate loading the validated result without changing combat accounting. Reuse the [native probe workflow](../Tests/ck3_probe/README.md), [Attila calibration workflow](../Tests/AttilaCalibration/README.md), and [isolated runtime](../Tests/DevelopmentRuntime/README.md) when implementation is requested.
