using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.unit_mapper;
using CrusaderWars.armies;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CrusaderWars.StatFidelity
{
    public sealed class StatFidelitySettings
    {
        public double MinimumMultiplier { get; set; } = 0.01;
        public double MaximumMultiplier { get; set; } = 1000;
        public int CommanderBodyguardSoldiers { get; set; } = 2;
        public bool RequireCompleteRegimentCoverage { get; set; } = true;
        public bool RequireCompleteLevyCoverage { get; set; } = true;
        public bool RequireCompleteExport { get; set; } = true;
        // Independent native knight budgets are included in the fidelity option.
        // Advanced users can explicitly opt out; absolute combat response remains experimental.
        public bool EnableKnightCalibration { get; set; } = true;
        public string KnightBudgetMode { get; set; } = "Anchored";
        public string KnightTemplateOverride { get; set; } = "";
        public double KnightMaximumMultiplier { get; set; } = 25;
        public int KnightMinimumBodies { get; set; } = 2;
        public int KnightMaximumBodies { get; set; } = 10000;
        public int KnightMaximumFormationSize { get; set; } = 0; // 0 means the active template's nominal formation size.
        public double KnightDamageResponseFactor { get; set; } = 1;
        public double KnightToughnessResponseFactor { get; set; } = 1;
        public string KnightCalibrationEvidence { get; set; } = "nominal-stat anchor only; combat response not fitted";
        public int KnightBodiesPerCharacter { get; set; } = 4;
        public double KnightReferenceProwess { get; set; } = 10;
        public double KnightReferenceDamagePerProwess { get; set; } = 50;
        public double KnightReferenceToughnessPerProwess { get; set; } = 10;

        public void Validate()
        {
            if (KnightBudgetMode != "Anchored" && KnightBudgetMode != "TemplateRelative")
                throw new InvalidOperationException("KnightBudgetMode must be Anchored or the explicitly uncalibrated TemplateRelative test mode.");
            if (!Finite(KnightMaximumMultiplier) || KnightMaximumMultiplier < MinimumMultiplier ||
                KnightMinimumBodies < 2 || KnightMaximumBodies < KnightMinimumBodies || KnightMaximumFormationSize < 0 || KnightMaximumFormationSize == 1 ||
                !Finite(KnightDamageResponseFactor) || KnightDamageResponseFactor <= 0 || !Finite(KnightToughnessResponseFactor) || KnightToughnessResponseFactor <= 0)
                throw new InvalidOperationException("Invalid anchored knight budget limits or response factors.");
            if (!Finite(MinimumMultiplier) || !Finite(MaximumMultiplier) || MinimumMultiplier <= 0 || MaximumMultiplier < MinimumMultiplier ||
                CommanderBodyguardSoldiers < 2 || CommanderBodyguardSoldiers > 100 || KnightBodiesPerCharacter < 2 || KnightBodiesPerCharacter > 100 || !Finite(KnightReferenceProwess) || KnightReferenceProwess <= 0 ||
                !Finite(KnightReferenceDamagePerProwess) || KnightReferenceDamagePerProwess <= 0 || !Finite(KnightReferenceToughnessPerProwess) || KnightReferenceToughnessPerProwess <= 0)
                throw new InvalidOperationException("Invalid settings/StatFidelity.json: positive finite bounds and knight calibration references are required.");
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class AppliedStatProfile
    {
        public string UnitKey { get; set; } = "";
        public string BaseUnitKey { get; set; } = "";
        public int? RepresentedSoldiers { get; set; }
        public int? MaximumFormationSoldiers { get; set; }
        public bool CommanderApplied { get; set; }
        public bool RegimentApplied { get; set; }
        public bool LevyApplied { get; set; }
        public bool KnightApplied { get; set; }
    }

    /// <summary>One generated battle only. Missing evidence is recorded, never inferred from unit names.</summary>
    public static class StatFidelitySession
    {
        private static readonly Dictionary<string, UnitStatProfile> profiles = new Dictionary<string, UnitStatProfile>(StringComparer.Ordinal);
        private static readonly HashSet<string> messages = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> coverage = new List<string>();
        private static readonly HashSet<string> preparedKnightArmies = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> calibratedKnightUnits = new HashSet<int>();
        private static readonly Dictionary<string, List<RegimentStats>> regimentLookup = new Dictionary<string, List<RegimentStats>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, KnightStats> commanderKnightBudgets = new Dictionary<string, KnightStats>(StringComparer.Ordinal);
        private static readonly Dictionary<string, KnightBudgetPlan> knightBudgetPlans = new Dictionary<string, KnightBudgetPlan>(StringComparer.Ordinal);
        private static readonly List<DeployedFormation> deployedRoster = new();
        private static readonly List<object> deployedSourceBindings = new();
        public static IReadOnlyList<DeployedFormation> DeployedRoster => deployedRoster.AsReadOnly();
        public static PreparedAttilaDatabase? PreparedDatabase { get; private set; }
        public static bool Enabled { get; private set; }
        public static bool SaveEvidenceValidated { get; private set; }
        public static StatExport Export { get; private set; } = new StatExport();
        public static StatFidelitySettings Settings { get; private set; } = new StatFidelitySettings();
        public static IReadOnlyCollection<UnitStatProfile> PendingProfiles => profiles.Values.ToArray();
        public static bool HasBoundExport => Enabled && Export.Complete && SaveEvidenceValidated;
        public static bool PreserveRegimentIdentity => Enabled && Export.Complete && SaveEvidenceValidated && Export.Regiments.Count > 0;
        public static bool UsesDirectCommanderStats(string side) => HasBoundExport && StatScaling.TryCommanderMultiplier(Export, side, out _);

        public static void Begin(string log)
        {
            profiles.Clear(); messages.Clear(); coverage.Clear(); preparedKnightArmies.Clear(); calibratedKnightUnits.Clear(); regimentLookup.Clear();
            commanderKnightBudgets.Clear();
            knightBudgetPlans.Clear(); deployedRoster.Clear(); deployedSourceBindings.Clear(); PreparedDatabase = null;
            SaveEvidenceValidated = false;
            Enabled = ModOptions.optionsValuesCollection.TryGetValue("StatFidelityMode", out string? mode) && (mode == "Validated" || mode == "Enabled");
            Export = Enabled ? StatExportParser.Parse(log) : new StatExport();
            Settings = new StatFidelitySettings();
            if (!Enabled) return;
            string configPath = Path.Combine("settings", "StatFidelity.json");
            if (File.Exists(configPath))
                Settings = JsonSerializer.Deserialize<StatFidelitySettings>(File.ReadAllText(configPath)) ?? throw new InvalidOperationException("Empty stat fidelity settings.");
            Settings.Validate();
            foreach (string message in Export.Diagnostics) Note(message);
            if (Settings.RequireCompleteExport && (!Export.Complete || Export.Diagnostics.Count > 0))
                throw Failure("CK3 combat stats require a complete valid CCSTAT v1 snapshot. " + string.Join(" ", Export.Diagnostics.Take(5)) +
                    " Disable CK3 combat stats to use legacy conversion, or regenerate the handoff with the matching CK3 component.");
            if (Settings.RequireCompleteExport && !StatScaling.TryCommanderMultiplier(Export, "attacker", out _))
                throw Failure("CK3 combat stats require commander evidence for both attacker and defender with the same active advantage coefficient (use a verified zero for a commanderless side). Disable CK3 combat stats to use legacy conversion.");
            foreach (var row in Export.Regiments)
            {
                string key = row.Side + ":" + row.RegimentId + ":" + row.OwnerId;
                if (!regimentLookup.TryGetValue(key, out var rows)) regimentLookup[key] = rows = new List<RegimentStats>();
                rows.Add(row);
            }
            if (Export.Complete)
                Note($"Stat export {Export.BattleId}: {Export.Regiments.Count} regiment, {Export.Levies.Count} levy-owner, {Export.Commanders.Count} commander and {Export.Knights.Count} knight records accepted. Regiment ratios use owner/optional province/title getter contexts; context coverage requires engine tests. This is relative template scaling, not verified absolute CK3 troop balance. Combat-only counters, terrain and conditional modifiers need separate verification. Pursuit/screen are recorded but not converted; Attila resolves battlefield movement and pursuit.");
        }

        public static void Note(string message)
        {
            if (messages.Add(message)) Program.Logger.Debug("[Stat fidelity] " + message);
        }

        public static InvalidOperationException InvalidateHandoff(string reason)
        {
            profiles.Clear(); messages.Clear(); coverage.Clear(); preparedKnightArmies.Clear(); calibratedKnightUnits.Clear();
            regimentLookup.Clear(); commanderKnightBudgets.Clear(); knightBudgetPlans.Clear(); deployedRoster.Clear(); deployedSourceBindings.Clear();
            PreparedDatabase = null; SaveEvidenceValidated = false; Export = new StatExport(); Settings = new StatFidelitySettings();
            Enabled = ModOptions.optionsValuesCollection.TryGetValue("StatFidelityMode", out string? mode) && (mode == "Enabled" || mode == "Validated");
            return Failure(reason);
        }

        public static void ValidateSaveEvidence(TextReader living, string playerId, string combatId)
        {
            if (!Enabled || !Export.Complete) return;
            string reason;
            try { SaveEvidenceValidated = SnapshotEvidence.Validate(living, playerId, Export.BattleId, combatId, out reason); }
            catch (RegexMatchTimeoutException) { SaveEvidenceValidated = false; reason = "Snapshot freshness rejected: bounded save parsing timed out."; }
            Note(reason);
            if (!SaveEvidenceValidated && Settings.RequireCompleteExport)
                throw Failure(reason + " Regenerate the handoff snapshot and save together, or disable CK3 combat stats to use legacy conversion.");
        }

        public static void ValidateSaveEvidence(string livingPath, string playerId, string combatId)
        {
            if (!Enabled || !Export.Complete) return;
            try { using (var reader = new StreamReader(livingPath)) ValidateSaveEvidence(reader, playerId, combatId); }
            catch (IOException ex)
            {
                SaveEvidenceValidated = false;
                string reason = "Snapshot freshness rejected: cannot read the handoff save's living-character data. " + ex.Message;
                if (Settings.RequireCompleteExport) throw Failure(reason);
                Note(reason);
            }
        }

        public static void WriteCoverageReport(string runtimeRoot)
        {
            if (!Enabled) return;
            string path = Path.Combine(runtimeRoot, "data", "stat_fidelity_coverage.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, battleId = Export.BattleId, complete = Export.Complete, saveEvidenceValidated = SaveEvidenceValidated,
                acceptedRegiments = Export.Regiments.Count, acceptedLevies = Export.Levies.Count, acceptedCommanders = Export.Commanders.Count, acceptedKnights = Export.Knights.Count,
                generatedProfiles = profiles.Count, deployedFormations = deployedSourceBindings, settings = Settings,
                scope = new {
                    regimentScaling = "effective/base ratios on mapped Attila templates",
                    commanderScaling = "difference of exported character commander advantage; not complete combat advantage",
                    knightScaling = !Settings.EnableKnightCalibration ? "native roster with legacy combat representation" : Settings.KnightBudgetMode == "Anchored"
                        ? "independent nominal damage/toughness budgets anchored to same-army melee MAA or verified owner levy; response factors require battle fitting"
                        : "explicit uncalibrated template-relative test mode",
                    absoluteBudgetCalibrationVerified = false,
                    levyScaling = Export.Levies.Count > 0 ? "native owner effective/base ratios matched to exact saved army owner and side" : "mapped base stats; no native levy-owner export",
                    unconverted = new[] { "MAA counters", "combat terrain/width modifiers", "combat rolls", "pursuit/screen stats", "siege progress", "independent garrison stats" }
                }, knightBudgets = knightBudgetPlans.Values.ToArray(), coverage, diagnostics = messages.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static void CaptureDeployedRoster(IEnumerable<Army> armies, string battleXml, bool archivedNativeBattle = false)
        {
            if (!HasBoundExport) return;
            if (archivedNativeBattle && !client.DevelopmentRuntime.Enabled)
                throw Failure("Archived deployed roster binding is restricted to an explicit isolated development replay.");
            deployedRoster.Clear();
            deployedSourceBindings.Clear();
            var units = new Dictionary<int, Unit>();
            var visited = new HashSet<Army>();
            void visit(Army army)
            {
                if (!visited.Add(army)) return;
                foreach (var unit in army.Units)
                {
                    if (units.TryGetValue(unit.UniqueID, out var previous) && !ReferenceEquals(previous, unit))
                        throw Failure("Ambiguous generated unit identity " + unit.UniqueID + ". No result roster was inferred.");
                    units[unit.UniqueID] = unit;
                }
                foreach (var merged in army.MergedArmies ?? new List<Army>()) visit(merged);
            }
            foreach (var army in armies) visit(army);
            var emittedNames = new HashSet<string>(StringComparer.Ordinal);
            var emittedIds = new HashSet<int>();
            foreach (var element in XDocument.Load(battleXml).Descendants("unit"))
            {
                string script = element.Attribute("script_name")?.Value ?? "";
                var identity = Regex.Match(script, @"_(attacker|defender)_army(?<army>\d+)_TYPE.*_(?<unit>\d+)_CULTURE", RegexOptions.CultureInvariant);
                if (!identity.Success || !int.TryParse(identity.Groups["unit"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int unitId) ||
                    !int.TryParse(element.Attribute("num_soldiers")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int bodies) || bodies <= 0 ||
                    !units.TryGetValue(unitId, out var source) || !emittedNames.Add(script) || !emittedIds.Add(unitId))
                    throw Failure("Generated formation cannot be bound uniquely to its source unit: " + script + ". Save result import is disabled.");
                deployedRoster.Add(new DeployedFormation(script, bodies));
                deployedSourceBindings.Add(new {
                    scriptName = script, deployedBodies = bodies, unitId,
                    armyId = identity.Groups["army"].Value, unitType = source.GetRegimentType().ToString(),
                    source.SourceIdentity, source.SourceOwnerId, source.SourceOriginProvinceId,
                    emittedUnitKey = element.Element("unit_type")?.Attribute("type")?.Value
                });
            }
            if (deployedRoster.Count == 0) throw Failure("The deployed native roster is empty.");
            var generatedNames = Data.units_scripts.Select(u => u.unitName).ToHashSet(StringComparer.Ordinal);
            if (!emittedNames.SetEquals(generatedNames))
            {
                if (!archivedNativeBattle) throw Failure("Generated XML and native survivor-report script registries differ. No partial result roster was accepted.");
                // A corrected offline importer can read the original native battle's
                // generic-general culture token. Never rewrite the raw AAR identity.
                var presentationMissing = emittedNames.Except(generatedNames).ToArray();
                var extra = generatedNames.Except(emittedNames).ToArray();
                string identityPrefix(string name) => name[..name.IndexOf("_CULTURE", StringComparison.Ordinal)];
                if (presentationMissing.Length != extra.Length || presentationMissing.Any(name => !name.Contains("_TYPEcommandercc_untracked_", StringComparison.Ordinal) ||
                    extra.Count(other => identityPrefix(other) == identityPrefix(name)) != 1))
                    throw Failure("Archived native source-map identities differ from the current replay beyond the documented untracked-general culture repair. No correspondence was synthesized.");
                foreach (var name in presentationMissing)
                    Note("Archived native identity retained verbatim: " + name + "; corrected future generic presentation: " + extra.Single(other => identityPrefix(other) == identityPrefix(name)));
            }
            var missing = units.Values.Where(u => u.GetSoldiers() > 0 && !emittedIds.Contains(u.UniqueID)).Select(u => u.UniqueID).ToArray();
            if (missing.Length > 0)
                throw Failure("Source units have no emitted formation: " + string.Join(",", missing) + ". No source-map coverage was synthesized.");
            Note("Bound " + deployedRoster.Count + " emitted formations to exact source units and raw deployed body counts, including levy, general and siege representations.");
        }

        public static ValidatedBattleReports? ValidateResultReports(IReadOnlyList<string> survivors, IReadOnlyList<string> kills)
        {
            if (!HasBoundExport) return null;
            if (deployedRoster.Count == 0)
                throw Failure("No exact deployed formation roster is available for these results. Restart Continue Battle to regenerate the bound battle before importing results.");
            try { return BattleReportEvidence.Validate(deployedRoster, survivors, kills); }
            catch (InvalidDataException exception) { throw Failure("Native battle reports do not match the complete deployed roster: " + exception.Message); }
        }

        public static AppliedStatProfile Apply(Unit unit, Army army, string baseKey)
        {
            var result = new AppliedStatProfile { UnitKey = baseKey, BaseUnitKey = baseKey };
            if (Enabled && Export.Complete && !SaveEvidenceValidated && Settings.RequireCompleteExport)
                throw Failure("CK3 combat stats have not been bound to the selected handoff save. Regenerate the snapshot/save or disable CK3 combat stats to use legacy conversion.");
            if (!Enabled || !Export.Complete || !SaveEvidenceValidated) return result;
            if (unit.IsSiege())
            {
                Note("Siege engines retain legacy mapping; field damage cannot be inferred from CK3 siege progress.");
                return result;
            }
            if ((unit.GetRegimentType() == RegimentType.MenAtArms || (unit.GetRegimentType() == RegimentType.Levy && Export.Levies.Count > 0)) &&
                (string.IsNullOrWhiteSpace(baseKey) || baseKey == UnitMappers_BETA.NOT_FOUND_KEY))
            {
                string message = "No Attila mapping for participating " + unit.GetRegimentType() + " " + (unit.SourceIdentity ?? army.ID + ":" + unit.GetName());
                if (Settings.RequireCompleteRegimentCoverage || (unit.GetRegimentType() == RegimentType.Levy && Settings.RequireCompleteLevyCoverage))
                    throw Failure(message + ". Strict CK3 combat stats cannot drop a source regiment from the battle. Repair the mapping or disable CK3 combat stats to use legacy conversion.");
                Note(message + "; explicit partial-coverage configuration allows the legacy unmapped-unit behavior.");
                return result;
            }
            double damage = 1, hp = 1;
            bool commander = StatScaling.TryCommanderMultiplier(Export, army.CombatSide, out damage);
            if (!commander && Settings.RequireCompleteExport)
                throw Failure("Commander evidence is incomplete for " + army.CombatSide + ". Disable CK3 combat stats to use legacy conversion.");
            result.CommanderApplied = commander;
            if (!commander) Note("Commander scaling unavailable: both sides need valid advantage and the same active damage coefficient; legacy commander/trait XP retained.");
            RegimentStats? stats = null;
            if (unit.SourceRegimentId != null && regimentLookup.TryGetValue(army.CombatSide + ":" + unit.SourceRegimentId + ":" + unit.SourceOwnerId, out var candidates))
            {
                var matches = candidates.Where(r => (r.ArmyId == "none" || r.ArmyId == unit.SourceArmyId) && (r.Type == "source" || r.Type == unit.GetName())).ToArray();
                if (matches.Length > 1) throw Failure("Ambiguous stat export rows match source regiment " + unit.SourceIdentity + ". No row was selected.");
                stats = matches.SingleOrDefault();
            }
            if (stats != null)
            {
                if (unit.SourceOriginProvinceId == null || unit.SourceOriginProvinceId != stats.StationProvinceId)
                    throw Failure("Source origin/station province does not match the exported stat context for regiment " + unit.SourceIdentity +
                        ". Saved origin=" + (unit.SourceOriginProvinceId ?? "unavailable") + ", export=" + stats.StationProvinceId + ". No stale station bonus was applied.");
                if (stats.BaseDamage <= 0 || stats.BaseToughness <= 0)
                    throw Failure("Source regiment " + stats.Identity + " has zero base damage or toughness and cannot define a combat-unit ratio. No baseline was invented.");
                (damage, hp) = StatScaling.Regiment(stats, damage);
                result.RegimentApplied = true;
            }
            else if (unit.GetRegimentType() == RegimentType.MenAtArms)
            {
                string message = "No exact effective/base stat record for MAA " + (unit.SourceIdentity ?? army.ID + ":" + unit.GetName());
                if (Settings.RequireCompleteRegimentCoverage)
                    throw Failure(message + ". Stat fidelity requires complete participating-regiment coverage. No partially upgraded battle was generated; inspect the export diagnostics or disable the experimental mode.");
                Note(message + "; explicit partial-coverage configuration retains its mapped base stats.");
            }
            if (unit.GetRegimentType() == RegimentType.Levy && !army.IsGarrisonArmy && (Settings.RequireCompleteLevyCoverage || Export.Levies.Count > 0))
            {
                string owner = army.Owner?.GetID() ?? "";
                if (string.IsNullOrEmpty(owner) || !Export.Levies.TryGetValue(army.CombatSide + ":" + owner, out var levy))
                {
                    if (Settings.RequireCompleteLevyCoverage || Settings.RequireCompleteRegimentCoverage)
                        throw Failure("No exact levy-owner stat record for " + army.CombatSide + " army " + army.ID + " owner " + owner +
                            ". Run Fight again with the matching CK3 component. A partially upgraded levy battle was not generated.");
                    Note("Army " + army.ID + " lacks native levy-owner evidence; explicit partial coverage retains mapped base levy stats.");
                }
                else
                {
                    damage *= levy.Damage / levy.BaseDamage;
                    hp = levy.Toughness / levy.BaseToughness;
                    result.LevyApplied = true;
                }
            }
            if (unit.GetRegimentType() == RegimentType.Commander && commanderKnightBudgets.TryGetValue(army.ID, out var fightingCommander))
            {
                if (Settings.KnightBudgetMode == "Anchored")
                {
                    var plan = PlanKnightBudget(army, baseKey, new[] { fightingCommander });
                    if (plan.Bodies > 0)
                    {
                        baseKey = plan.TemplateKey;
                        result.BaseUnitKey = baseKey;
                        result.RepresentedSoldiers = plan.Bodies;
                        damage = plan.DamageMultiplier;
                        hp = plan.HitPointMultiplier;
                        result.KnightApplied = true;
                    }
                }
                else
                {
                    // Explicit legacy calibration-test mode keeps the character once.
                    double representation = (double)Settings.KnightBodiesPerCharacter / Settings.CommanderBodyguardSoldiers;
                    damage *= fightingCommander.Prowess * fightingCommander.Effectiveness * fightingCommander.DamagePerProwess /
                        (Settings.KnightReferenceProwess * Settings.KnightReferenceDamagePerProwess) * representation;
                    hp = fightingCommander.Prowess * fightingCommander.Effectiveness * fightingCommander.ToughnessPerProwess /
                        (Settings.KnightReferenceProwess * Settings.KnightReferenceToughnessPerProwess) * representation;
                    result.KnightApplied = true;
                    Note("Verified fighting commander " + fightingCommander.CharacterId + " contributes one template-relative test budget through its general formation; no duplicate knight/fate roll.");
                }
            }
            if (!commander && stats == null && !result.LevyApplied && !result.KnightApplied) return result;
            string identity = unit.SourceIdentity ?? (army.CombatSide + ":" + army.ID + ":" + unit.GetRegimentType());
            result.UnitKey = Register(identity + ":" + baseKey, baseKey, damage, hp);
            coverage.Add(identity + ": " + (stats != null ? "effective regiment stats" : result.LevyApplied ? "effective levy owner stats" : "mapped base stats") + (commander ? "; commander advantage" : "; legacy commander XP"));
            return result;
        }

        public static bool HasCompleteKnightEvidence(Army army)
        {
            if (!Enabled || !Export.Complete || !SaveEvidenceValidated || !Settings.EnableKnightCalibration || army.Knights == null) return false;
            var knights = army.Knights.GetKnightsList();
            bool complete = knights.Count > 0 && knights.All(k => Export.Knights.ContainsKey(army.CombatSide + ":" + k.GetID()));
            if (knights.Count > 0 && !complete && Settings.RequireCompleteExport)
                throw Failure("Knight calibration was enabled but the original knight roster for army " + army.ID + " lacks complete prowess/efficiency evidence. Disable knight calibration or CK3 combat stats to use legacy conversion.");
            return complete;
        }

        public static bool EnsureNativeCommanderRepresentation(Army army)
        {
            if (!HasBoundExport || army.IsGarrisonArmy) return false;
            bool noSideCommander = army.isMainArmy && Export.Commanders.TryGetValue(army.CombatSide, out var commander) && commander.CharacterId == "none";
            bool noArmyCommander = string.IsNullOrEmpty(army.CommanderID) || army.CommanderID is "none" or "4294967295";
            if (!noSideCommander && (army.isMainArmy || !noArmyCommander)) return false;
            if (noSideCommander && !noArmyCommander)
                throw Failure("Native commanderless side conflicts with a saved main-army commander. No owner or other character was substituted.");
            army.CommanderID = null;
            army.SetCommander(CommanderSystem.CreateUntrackedGeneral(army.ID, army.Owner?.GetCulture() ?? throw Failure("Uncommanded army lacks a saved owner culture.")));
            Note("Army " + army.ID + " uses an untracked generic engine general. Its loss cannot kill, wound or capture a CK3 character.");
            return true;
        }

        public static bool TryCreateNativeKnights(Army army, List<(string id, string prowess, string name, int effectiveness)> legacyNames, out KnightSystem knights)
        {
            knights = new KnightSystem(new List<Knight>(), 0);
            if (!Enabled || !Export.Complete || !SaveEvidenceValidated) return false;
            var roster = army.ArmyRegiments.Where(r => r.Type == RegimentType.Knight && r.MAA_Name != army.CommanderID).ToArray();
            var rows = new List<KnightStats>();
            foreach (var regiment in roster)
            {
                if (!Export.Knights.TryGetValue(army.CombatSide + ":" + regiment.MAA_Name, out var row))
                {
                    if (Settings.RequireCompleteExport) throw Failure("Native knight roster lacks stat evidence for " + army.CombatSide + ":" + regiment.MAA_Name + ". No character was silently omitted.");
                    return false;
                }
                rows.Add(row);
            }
            if (!Settings.EnableKnightCalibration && rows.Select(r => r.Effectiveness).Distinct().Count() > 1)
                throw Failure("Different native knight efficiencies in army " + army.ID + " cannot use a single legacy efficiency. Enable calibrated knight conversion or disable CK3 combat stats.");
            var characters = rows.Select(row => new Knight(
                legacyNames.FirstOrDefault(k => k.id == row.CharacterId).name ?? "Knight " + row.CharacterId,
                row.CharacterId, null!, checked((int)row.Prowess), 4)).ToList();
            if (Settings.EnableKnightCalibration)
            {
                bool anyStrength = rows.Any(row => row.Prowess > 0 && row.Effectiveness > 0);
                foreach (var character in characters)
                    character.FidelitySoldierCount = anyStrength ? Settings.KnightBodiesPerCharacter : 0;
            }
            // Legacy XP adds this percentage to its base. Native evidence supplies a
            // total multiplier, so subtract 1 before expressing that additive bonus.
            int legacyBonus = rows.Count == 0 ? 0 : checked((int)Math.Round((rows[0].Effectiveness - 1) * 100));
            knights = new KnightSystem(characters, legacyBonus);
            Note("Army " + army.ID + ": " + characters.Count + " knights restored from native roster/stat evidence; localized tooltip parsing does not decide membership.");
            return true;
        }

        public static void CaptureCommanderKnightBudget(Army army)
        {
            if (!Enabled || !Export.Complete || !SaveEvidenceValidated || !Settings.EnableKnightCalibration || string.IsNullOrEmpty(army.CommanderID)) return;
            var roster = army.ArmyRegiments.Where(r => r.Type == RegimentType.Knight).ToList();
            if (!roster.Any(r => r.MAA_Name == army.CommanderID)) return;
            if (!roster.All(r => Export.Knights.ContainsKey(army.CombatSide + ":" + r.MAA_Name)))
            {
                if (Settings.RequireCompleteExport) throw Failure("Fighting-commander knight calibration requires complete original roster evidence for army " + army.ID + ".");
                return;
            }
            commanderKnightBudgets[army.ID] = Export.Knights[army.CombatSide + ":" + army.CommanderID];
        }

        public static bool TryApplyKnights(Army army, string baseKey, out AppliedStatProfile profile, out int soldiers)
        {
            if (HasBoundExport && Settings.EnableKnightCalibration && !string.IsNullOrWhiteSpace(Settings.KnightTemplateOverride))
                baseKey = Settings.KnightTemplateOverride;
            profile = new AppliedStatProfile { UnitKey = baseKey, BaseUnitKey = baseKey };
            soldiers = army.Knights?.GetKnightsSoldiers() ?? 0;
            if (HasBoundExport && Settings.EnableKnightCalibration && Settings.RequireCompleteExport &&
                (string.IsNullOrWhiteSpace(baseKey) || baseKey == UnitMappers_BETA.NOT_FOUND_KEY))
                throw Failure("A verified knight roster has no mapped combat template in army " + army.ID + ". No knight characters were dropped from the battle.");
            if (!preparedKnightArmies.Contains(army.ID) || !HasCompleteKnightEvidence(army) || string.IsNullOrWhiteSpace(baseKey) || baseKey == UnitMappers_BETA.NOT_FOUND_KEY)
            {
                if (Enabled) Note("Knights retain legacy representation: complete knight evidence and explicit EnableKnightCalibration are required. The reference template is not battle-calibrated.");
                return false;
            }
            var knights = army.Knights!.GetKnightsList();
            var rows = knights.Select(k => Export.Knights[army.CombatSide + ":" + k.GetID()]).ToArray();
            if (Settings.KnightBudgetMode == "Anchored")
            {
                var plan = PlanKnightBudget(army, baseKey, rows);
                foreach (var knight in knights)
                    knight.FidelitySoldierCount = plan.Characters.Single(c => c.CharacterId == knight.GetID()).Bodies;
                soldiers = plan.Bodies;
                profile.KnightApplied = true;
                profile.CommanderApplied = true;
                profile.BaseUnitKey = plan.TemplateKey;
                profile.RepresentedSoldiers = soldiers;
                var template = PreparedDatabase!.Query(plan.TemplateKey);
                int nominal = template.NominalSoldiers;
                if (nominal < 2) throw Failure("Knight template has no supported nominal formation size: " + plan.TemplateKey);
                profile.MaximumFormationSoldiers = Settings.KnightMaximumFormationSize == 0 ? nominal : Math.Min(nominal, Settings.KnightMaximumFormationSize);
                if (soldiers == 0) return true;
                string anchoredIds = string.Join("-", rows.Select(k => k.CharacterId).OrderBy(x => x, StringComparer.Ordinal));
                profile.UnitKey = Register("knights:" + army.CombatSide + ":" + army.ID + ":" + anchoredIds + ":" + plan.TemplateKey,
                    plan.TemplateKey, plan.DamageMultiplier, plan.HitPointMultiplier);
                coverage.Add("Army " + army.ID + ": " + rows.Length + " native knights represented by " + soldiers + " retinue bodies; independent nominal D/T budgets anchored to " + plan.Anchor.SourceIdentity + ". " + plan.CalibrationEvidence);
                return true;
            }
            double damageTotal = rows.Sum(k => k.Prowess * k.Effectiveness * k.DamagePerProwess);
            double hpTotal = rows.Sum(k => k.Prowess * k.Effectiveness * k.ToughnessPerProwess);
            if (damageTotal == 0 && hpTotal == 0)
            {
                foreach (var knight in knights) knight.FidelitySoldierCount = 0;
                soldiers = 0;
                profile.KnightApplied = true;
                Note("Zero effective knight strength in army " + army.ID + ": no fighting unit generated.");
                return true;
            }
            double damage = damageTotal / (knights.Count * Settings.KnightReferenceProwess * Settings.KnightReferenceDamagePerProwess);
            double hp = hpTotal / (knights.Count * Settings.KnightReferenceProwess * Settings.KnightReferenceToughnessPerProwess);
            profile.CommanderApplied = StatScaling.TryCommanderMultiplier(Export, army.CombatSide, out double commander);
            damage *= commander;
            string ids = string.Join("-", rows.Select(k => k.CharacterId).OrderBy(x => x, StringComparer.Ordinal));
            profile.UnitKey = Register("knights:" + army.CombatSide + ":" + army.ID + ":" + ids + ":" + baseKey, baseKey, damage, hp);
            profile.KnightApplied = true;
            foreach (var knight in knights) knight.FidelitySoldierCount = Settings.KnightBodiesPerCharacter;
            soldiers = checked(knights.Count * Settings.KnightBodiesPerCharacter);
            coverage.Add("Army " + army.ID + ": calibrated aggregate knight profile, " + knights.Count + " characters; prowess x effective efficiency applied once to damage and toughness, no prowess XP.");
            return true;
        }

        private static string Register(string id, string baseKey, double damage, double hitPoints)
        {
            CheckMultiplier(damage, "damage", id); CheckMultiplier(hitPoints, "hit points", id);
            var profile = new UnitStatProfile(id, baseKey, damage, hitPoints);
            if (profiles.TryGetValue(id, out var previous) && (previous.DamageMultiplier != damage || previous.HitPointMultiplier != hitPoints || previous.BaseUnitKey != baseKey))
                throw Failure("Conflicting stat profiles for " + id);
            profiles[id] = profile;
            return StatFidelityBackend.GeneratedKey(profile);
        }

        public static bool PrepareKnightCalibration(Army army)
        {
            var armies = new[] { army }.Concat(army.MergedArmies ?? new List<Army>()).Where(a => a.Knights != null && a.Knights.HasKnights()).ToList();
            if (armies.Count == 0 || !armies.All(HasCompleteKnightEvidence)) return false;
            foreach (var item in armies) preparedKnightArmies.Add(item.ID);
            return true;
        }

        public static void RegisterCalibratedKnightUnit(int uniqueId) => calibratedKnightUnits.Add(uniqueId);
        public static void ResetGeneration()
        {
            deployedRoster.Clear(); deployedSourceBindings.Clear();
            profiles.Clear(); coverage.Clear(); preparedKnightArmies.Clear(); calibratedKnightUnits.Clear();
            knightBudgetPlans.Clear();
        }

        public static void PrepareKnightBudgetDatabase(string runtimeRoot, string attilaExecutable, string? cacheRoot = null)
        {
            if (!HasBoundExport || !Settings.EnableKnightCalibration || Settings.KnightBudgetMode != "Anchored" || Export.Knights.Count == 0) return;
            if (PreparedDatabase == null) PreparedDatabase = StatFidelityBackend.Prepare(runtimeRoot, attilaExecutable, cacheRoot);
            else PreparedDatabase.AssertFresh();
        }

        private static KnightBudgetPlan PlanKnightBudget(Army army, string mappedKey, IReadOnlyList<KnightStats> rows)
        {
            if (PreparedDatabase == null)
                throw Failure("Anchored knight budgets require the active Attila database before generating battle units.");
            var candidates = new List<(Unit unit, RegimentStats stats, AttilaUnitCombatStats template)>();
            foreach (var unit in army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms && !u.IsSiege() && u.SourceRegimentId != null && u.SourceArmyId == army.ID))
            {
                if (!regimentLookup.TryGetValue(army.CombatSide + ":" + unit.SourceRegimentId + ":" + unit.SourceOwnerId, out var records)) continue;
                var matching = records.Where(r => (r.ArmyId == "none" || r.ArmyId == unit.SourceArmyId) && (r.Type == "source" || r.Type == unit.GetName())).ToArray();
                if (matching.Length != 1 || matching[0].Damage <= 0 || matching[0].Toughness <= 0 || matching[0].BaseDamage <= 0 || matching[0].BaseToughness <= 0) continue;
                if (unit.SourceOriginProvinceId != matching[0].StationProvinceId) continue;
                string key = unit.GetAttilaUnitKey();
                if (string.IsNullOrWhiteSpace(key) || key == UnitMappers_BETA.NOT_FOUND_KEY) continue;
                var template = PreparedDatabase.Query(key);
                if (template.HasMissileWeapon || template.EffectiveInfantryHitPoints == null || template.TotalMeleeDamage <= 0) continue;
                candidates.Add((unit, matching[0], template));
            }
            if (candidates.Count == 0)
            {
                string owner = army.Owner?.GetID() ?? "";
                if (!army.IsGarrisonArmy && Export.Levies.TryGetValue(army.CombatSide + ":" + owner, out var levy) && levy.Damage > 0 && levy.Toughness > 0)
                {
                    foreach (var unit in army.Units.Where(u => u.GetRegimentType() == RegimentType.Levy && u.GetSoldiers() > 0))
                    {
                        string key = unit.GetAttilaUnitKey();
                        if (string.IsNullOrWhiteSpace(key) || key == UnitMappers_BETA.NOT_FOUND_KEY) continue;
                        var template = PreparedDatabase.Query(key);
                        if (template.HasMissileWeapon || template.EffectiveInfantryHitPoints == null || template.TotalMeleeDamage <= 0) continue;
                        candidates.Add((unit, new RegimentStats { Side = army.CombatSide, OwnerId = owner, Damage = levy.Damage, Toughness = levy.Toughness,
                            BaseDamage = levy.BaseDamage, BaseToughness = levy.BaseToughness }, template));
                    }
                }
            }
            if (candidates.Count == 0)
                throw Failure("No verified foot-melee MAA or owner-levy anchor exists in army " + army.ID + ". Knight budgets cannot invent a reference or silently use another army's mapping.");
            // Regeneration may already have split one source regiment into multiple
            // formations. Anchor choice depends on the source's total represented
            // soldiers, not the size of whichever split happened to come first.
            var selected = candidates.GroupBy(c => (SourceIdentity: c.unit.SourceIdentity ?? "levy:" + c.stats.Side + ":" + c.stats.OwnerId, c.template.UnitKey))
                .OrderByDescending(g => g.Sum(c => c.unit.GetSoldiers()))
                .ThenBy(g => g.Key.SourceIdentity, StringComparer.Ordinal).First().First();
            double ratioD = selected.stats.Damage / selected.stats.BaseDamage;
            double ratioT = selected.stats.Toughness / selected.stats.BaseToughness;
            double anchorDamage = Math.Round(selected.template.MeleeDamage * ratioD, MidpointRounding.AwayFromZero) +
                Math.Round(selected.template.ArmourPiercingDamage * ratioD, MidpointRounding.AwayFromZero);
            double anchorHitPoints = Math.Round(selected.template.EffectiveInfantryHitPoints!.Value * ratioT, MidpointRounding.AwayFromZero);
            var anchor = new KnightBudgetAnchor(selected.unit.SourceIdentity ?? "levy:" + selected.stats.Side + ":" + selected.stats.OwnerId, selected.template.UnitKey, selected.stats.Damage,
                selected.stats.Toughness, anchorDamage, anchorHitPoints);
            string keyToUse = string.IsNullOrWhiteSpace(Settings.KnightTemplateOverride) ? mappedKey : Settings.KnightTemplateOverride;
            var target = PreparedDatabase.Query(keyToUse);
            if (target.IsNaval || target.HasSpecialEntity || target.HasMissileWeapon)
                throw Failure("Knight budget template must be a conventional melee combat formation: " + keyToUse);
            var budgetTemplate = new KnightBudgetTemplate(keyToUse, target.MeleeDamage, target.ArmourPiercingDamage,
                target.EffectiveHitPoints ?? checked(target.EntityHitPoints + target.BonusHitPoints), target.CanScaleHitPoints);
            if (!StatScaling.TryCommanderMultiplier(Export, army.CombatSide, out double commander))
                throw Failure("Knight budget has no complete commander evidence.");
            var plan = KnightBudgetPlanner.Plan(rows, anchor, budgetTemplate, commander, new KnightBudgetLimits {
                MinimumMultiplier = Settings.MinimumMultiplier, MaximumMultiplier = Math.Min(Settings.MaximumMultiplier, Settings.KnightMaximumMultiplier),
                MinimumBodies = Settings.KnightMinimumBodies, MaximumBodies = Settings.KnightMaximumBodies,
                DamageResponseFactor = Settings.KnightDamageResponseFactor, ToughnessResponseFactor = Settings.KnightToughnessResponseFactor,
                CalibrationEvidence = Settings.KnightCalibrationEvidence
            }, ArmyProportions.BattleScale / 100.0);
            string identity = army.ID + ":" + keyToUse + ":" + string.Join("-", rows.Select(r => r.CharacterId).OrderBy(x => x, StringComparer.Ordinal));
            knightBudgetPlans[identity] = plan;
            try { plan.RequireVerifiedHitPoints(); }
            catch (InvalidOperationException error) { throw Failure(error.Message); }
            return plan;
        }
        public static bool IsCalibratedKnightUnit(string type)
        {
            return type.StartsWith("knights_", StringComparison.Ordinal) && int.TryParse(type.Substring("knights_".Length), out int id) && calibratedKnightUnits.Contains(id);
        }
        public static bool IsKnightPoolType(string type) => type == "knights" ||
            (type.StartsWith("knights_", StringComparison.Ordinal) && int.TryParse(type.Substring("knights_".Length), out _));

        private static void CheckMultiplier(double value, string name, string id)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < Settings.MinimumMultiplier || value > Settings.MaximumMultiplier)
                throw Failure("Stat fidelity " + name + " multiplier " + value.ToString("G17", CultureInfo.InvariantCulture) + " for " + id +
                    " is outside configured bounds [" + Settings.MinimumMultiplier + ", " + Settings.MaximumMultiplier + "]. No clamping or stat substitution was performed.");
        }

        private static InvalidOperationException Failure(string message)
        {
            Note(message);
            try { WriteCoverageReport(Directory.GetCurrentDirectory()); }
            catch (IOException ex) { Note("Could not write the coverage report: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Note("Could not write the coverage report: " + ex.Message); }
            return new InvalidOperationException(message);
        }
    }
}
