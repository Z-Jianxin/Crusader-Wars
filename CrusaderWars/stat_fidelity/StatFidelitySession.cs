using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.unit_mapper;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrusaderWars.StatFidelity
{
    public sealed class StatFidelitySettings
    {
        public double MinimumMultiplier { get; set; } = 0.01;
        public double MaximumMultiplier { get; set; } = 1000;
        public int CommanderBodyguardSoldiers { get; set; } = 2;
        public bool RequireCompleteRegimentCoverage { get; set; } = true;
        public bool RequireCompleteExport { get; set; } = true;
        // A reference for calibration tests, not a claimed CK3-to-Attila equivalence.
        public bool EnableKnightCalibration { get; set; }
        public int KnightBodiesPerCharacter { get; set; } = 4;
        public double KnightReferenceProwess { get; set; } = 10;
        public double KnightReferenceDamagePerProwess { get; set; } = 50;
        public double KnightReferenceToughnessPerProwess { get; set; } = 10;

        public void Validate()
        {
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
        public bool CommanderApplied { get; set; }
        public bool RegimentApplied { get; set; }
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
        public static bool Enabled { get; private set; }
        public static bool SaveEvidenceValidated { get; private set; }
        public static StatExport Export { get; private set; } = new StatExport();
        public static StatFidelitySettings Settings { get; private set; } = new StatFidelitySettings();
        public static IReadOnlyCollection<UnitStatProfile> PendingProfiles => profiles.Values.ToArray();
        public static bool PreserveRegimentIdentity => Enabled && Export.Complete && SaveEvidenceValidated && Export.Regiments.Count > 0;

        public static void Begin(string log)
        {
            profiles.Clear(); messages.Clear(); coverage.Clear(); preparedKnightArmies.Clear(); calibratedKnightUnits.Clear(); regimentLookup.Clear();
            commanderKnightBudgets.Clear();
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
                Note($"Stat export {Export.BattleId}: {Export.Regiments.Count} regiment, {Export.Commanders.Count} commander and {Export.Knights.Count} knight records accepted. Regiment getters cover owner/station/title; combat-only counters, terrain and accolades need separate verification. Pursuit/screen are recorded but not converted; Attila resolves battlefield movement and pursuit.");
        }

        public static void Note(string message)
        {
            if (messages.Add(message)) Program.Logger.Debug("[Stat fidelity] " + message);
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
                acceptedRegiments = Export.Regiments.Count, acceptedCommanders = Export.Commanders.Count, acceptedKnights = Export.Knights.Count,
                generatedProfiles = profiles.Count, settings = Settings, coverage, diagnostics = messages.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static AppliedStatProfile Apply(Unit unit, Army army, string baseKey)
        {
            var result = new AppliedStatProfile { UnitKey = baseKey };
            if (Enabled && Export.Complete && !SaveEvidenceValidated && Settings.RequireCompleteExport)
                throw Failure("CK3 combat stats have not been bound to the selected handoff save. Regenerate the snapshot/save or disable CK3 combat stats to use legacy conversion.");
            if (!Enabled || !Export.Complete || !SaveEvidenceValidated) return result;
            if (unit.IsSiege())
            {
                Note("Siege engines retain legacy mapping; field damage cannot be inferred from CK3 siege progress.");
                return result;
            }
            if (unit.GetRegimentType() == RegimentType.MenAtArms &&
                (string.IsNullOrWhiteSpace(baseKey) || baseKey == UnitMappers_BETA.NOT_FOUND_KEY))
            {
                string message = "No Attila mapping for participating MAA " + (unit.SourceIdentity ?? army.ID + ":" + unit.GetName());
                if (Settings.RequireCompleteRegimentCoverage)
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
            if (unit.GetRegimentType() == RegimentType.Commander && commanderKnightBudgets.TryGetValue(army.ID, out var fightingCommander))
            {
                // This character exists once: its verified knight budget belongs to its
                // general formation, not an additional knight object with a second fate roll.
                double representation = (double)Settings.KnightBodiesPerCharacter / Settings.CommanderBodyguardSoldiers;
                damage *= fightingCommander.Prowess * fightingCommander.Effectiveness * fightingCommander.DamagePerProwess /
                    (Settings.KnightReferenceProwess * Settings.KnightReferenceDamagePerProwess) * representation;
                hp = fightingCommander.Prowess * fightingCommander.Effectiveness * fightingCommander.ToughnessPerProwess /
                    (Settings.KnightReferenceProwess * Settings.KnightReferenceToughnessPerProwess) * representation;
                result.KnightApplied = true;
                Note("Verified fighting commander " + fightingCommander.CharacterId + " contributes one calibrated knight budget through its general formation; no duplicate knight/fate roll.");
            }
            if (!commander && stats == null && !result.KnightApplied) return result;
            string identity = unit.SourceIdentity ?? (army.CombatSide + ":" + army.ID + ":" + unit.GetRegimentType());
            result.UnitKey = Register(identity + ":" + baseKey, baseKey, damage, hp);
            coverage.Add(identity + ": " + (stats != null ? "effective regiment stats" : "mapped base stats") + (commander ? "; commander advantage" : "; legacy commander XP"));
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
            profile = new AppliedStatProfile { UnitKey = baseKey };
            soldiers = army.Knights?.GetKnightsSoldiers() ?? 0;
            if (!preparedKnightArmies.Contains(army.ID) || !HasCompleteKnightEvidence(army) || string.IsNullOrWhiteSpace(baseKey) || baseKey == UnitMappers_BETA.NOT_FOUND_KEY)
            {
                if (Enabled) Note("Knights retain legacy representation: complete knight evidence and explicit EnableKnightCalibration are required. The reference template is not battle-calibrated.");
                return false;
            }
            var knights = army.Knights!.GetKnightsList();
            var rows = knights.Select(k => Export.Knights[army.CombatSide + ":" + k.GetID()]).ToArray();
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
            profiles.Clear(); coverage.Clear(); preparedKnightArmies.Clear(); calibratedKnightUnits.Clear();
        }
        public static bool IsCalibratedKnightUnit(string type)
        {
            return type.StartsWith("knights_", StringComparison.Ordinal) && int.TryParse(type.Substring("knights_".Length), out int id) && calibratedKnightUnits.Contains(id);
        }

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
