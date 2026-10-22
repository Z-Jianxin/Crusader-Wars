using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity
{
    public sealed class RegimentStats
    {
        public string Side { get; set; } = "";
        public string ArmyId { get; set; } = "";
        public string RegimentId { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public string Type { get; set; } = "";
        public string StationProvinceId { get; set; } = "";
        public double Damage { get; set; }
        public double Toughness { get; set; }
        public double Pursuit { get; set; }
        public double Screen { get; set; }
        public double BaseDamage { get; set; }
        public double BaseToughness { get; set; }
        public string Identity => Side + ":" + ArmyId + ":" + RegimentId + ":" + OwnerId;
    }

    public sealed class LevyStats
    {
        public string Side { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public double Damage { get; set; }
        public double Toughness { get; set; }
        public double BaseDamage { get; set; }
        public double BaseToughness { get; set; }
        public string Identity => Side + ":" + OwnerId;
    }

    public sealed class CommanderStats
    {
        public string Side { get; set; } = "";
        public string CharacterId { get; set; } = "";
        public double Advantage { get; set; }
        public double DamagePerAdvantage { get; set; }
    }

    public sealed class KnightStats
    {
        public string Side { get; set; } = "";
        public string CharacterId { get; set; } = "";
        public double Prowess { get; set; }
        // This is the total effective multiplier, not an additive percentage.
        public double Effectiveness { get; set; }
        public double DamagePerProwess { get; set; }
        public double ToughnessPerProwess { get; set; }
        // Derived from native combat participation; not a CCSTAT protocol field.
        public double ActiveFraction { get; set; } = 1;
    }

    public sealed class StatExport
    {
        public string BattleId { get; internal set; } = "";
        public bool Complete { get; internal set; }
        public List<RegimentStats> Regiments { get; } = new List<RegimentStats>();
        public Dictionary<string, LevyStats> Levies { get; } = new Dictionary<string, LevyStats>(StringComparer.Ordinal);
        public Dictionary<string, CommanderStats> Commanders { get; } = new Dictionary<string, CommanderStats>(StringComparer.Ordinal);
        public Dictionary<string, KnightStats> Knights { get; } = new Dictionary<string, KnightStats>(StringComparer.Ordinal);
        public List<string> Diagnostics { get; } = new List<string>();
    }

    /// <summary>
    /// Parses only the latest explicitly framed export. A truncated latest export must never
    /// revive an older battle's stats. The parser accepts no localized numbers or guessed IDs.
    /// </summary>
    public static class StatExportParser
    {
        public const int MaxLogCharacters = 32 * 1024 * 1024;
        public const int MaxRecordCharacters = 2048;
        public const int MaxRecords = 20000;
        private const string Prefix = "CCSTAT|";

        public static StatExport Parse(string log)
        {
            var result = new StatExport();
            if (log == null || log.Length > MaxLogCharacters)
            {
                result.Diagnostics.Add("Stat export rejected: input exceeds 32 MiB limit or is null.");
                return result;
            }
            bool inFrame = false;
            bool invalidFrame = false;
            int records = 0;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = new StringReader(log))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    int start = line.IndexOf(Prefix, StringComparison.Ordinal);
                    if (start < 0) continue;
                    string record = line.Substring(start).Trim();
                    if (record.Length > MaxRecordCharacters)
                    {
                        invalidFrame = true;
                        result.Diagnostics.Add("Stat export rejected: record exceeds 2048 characters.");
                        continue;
                    }
                    string[] fields = record.Split('|');
                    if (fields.Length >= 3 && fields[2] == "BEGIN")
                    {
                        result = new StatExport();
                        identities.Clear();
                        records = 0;
                        inFrame = true;
                        invalidFrame = fields.Length != 4 || fields[1] != "v1" || !Identifier(fields[3]);
                        if (!invalidFrame) result.BattleId = fields[3];
                        else result.Diagnostics.Add("Stat export rejected: unsupported version or invalid BEGIN record.");
                        continue;
                    }
                    if (!inFrame) continue;
                    if (++records > MaxRecords)
                    {
                        invalidFrame = true;
                        if (records == MaxRecords + 1) result.Diagnostics.Add("Stat export rejected: record count exceeds 20000.");
                        continue;
                    }
                    if (fields.Length < 3 || fields[1] != "v1")
                    {
                        invalidFrame = true;
                        result.Diagnostics.Add("Stat export rejected: mixed or unsupported schema version.");
                        continue;
                    }
                    if (fields[2] == "END")
                    {
                        result.Complete = !invalidFrame && fields.Length == 4 && fields[3] == result.BattleId;
                        if (!result.Complete) result.Diagnostics.Add("Stat export rejected: invalid frame or mismatched END.");
                        inFrame = false;
                        continue;
                    }
                    string reason;
                    if (!TryRecord(fields, result, identities, out reason))
                        result.Diagnostics.Add(reason);
                }
            }
            if (inFrame) result.Diagnostics.Add("Stat export incomplete: END record missing; legacy conversion retained.");
            if (result.BattleId.Length == 0) result.Diagnostics.Add("No versioned stat export available; legacy conversion retained.");
            if (!result.Complete)
            {
                result.Regiments.Clear(); result.Levies.Clear(); result.Commanders.Clear(); result.Knights.Clear();
            }
            return result;
        }

        private static bool TryRecord(string[] f, StatExport result, HashSet<string> identities, out string reason)
        {
            reason = "Stat record rejected (" + (f.Length > 2 ? f[2] : "unknown") + "): missing, invalid, or unknown data.";
            if (f[2] == "REGIMENT" && f.Length == 15 && Side(f[3]) &&
                Identifier(f[4]) && Identifier(f[5]) && Identifier(f[6]) && Identifier(f[7]) && Identifier(f[8]) &&
                Number(f[9], 0, 1000000, out double damage) && Number(f[10], 0, 1000000, out double toughness) &&
                Number(f[11], 0, 1000000, out double pursuit) && Number(f[12], 0, 1000000, out double screen) &&
                Number(f[13], 0, 1000000, out double baseDamage) && Number(f[14], 0, 1000000, out double baseToughness))
            {
                var row = new RegimentStats { Side = f[3], ArmyId = f[4], RegimentId = f[5], OwnerId = f[6], Type = f[7], StationProvinceId = f[8], Damage = damage, Toughness = toughness, Pursuit = pursuit, Screen = screen, BaseDamage = baseDamage, BaseToughness = baseToughness };
                if (!identities.Add("regiment:" + row.Identity))
                {
                    // Neither conflicting row is safe to use.
                    result.Regiments.RemoveAll(r => r.Identity == row.Identity);
                    reason = "Duplicate regiment identity rejected: " + row.Identity;
                    return false;
                }
                result.Regiments.Add(row);
                return true;
            }
            if (f[2] == "LEVY" && f.Length == 9 && Side(f[3]) && Identifier(f[4]) && f[4] != "none" &&
                Number(f[5], 0, 1000000, out double levyDamage) && Number(f[6], 0, 1000000, out double levyToughness) &&
                Number(f[7], 0.000001, 1000000, out double levyBaseDamage) && Number(f[8], 0.000001, 1000000, out double levyBaseToughness))
            {
                string key = f[3] + ":" + f[4];
                if (!identities.Add("levy:" + key))
                {
                    result.Levies.Remove(key); reason = "Duplicate levy owner/side rejected: " + key; return false;
                }
                result.Levies.Add(key, new LevyStats { Side = f[3], OwnerId = f[4], Damage = levyDamage, Toughness = levyToughness, BaseDamage = levyBaseDamage, BaseToughness = levyBaseToughness });
                return true;
            }
            if (f[2] == "COMMANDER" && f.Length == 7 && Side(f[3]) && Identifier(f[4]) &&
                Number(f[5], -1000, 1000, out double advantage) && Number(f[6], 0, 1, out double coefficient))
            {
                if (f[4] == "none" && advantage != 0) return false;
                if (!identities.Add("commander:" + f[3]))
                {
                    result.Commanders.Remove(f[3]); reason = "Duplicate commander side rejected: " + f[3]; return false;
                }
                result.Commanders.Add(f[3], new CommanderStats { Side = f[3], CharacterId = f[4], Advantage = advantage, DamagePerAdvantage = coefficient });
                return true;
            }
            if (f[2] == "KNIGHT" && f.Length == 9 && Side(f[3]) && Identifier(f[4]) &&
                Number(f[5], 0, 1000, out double prowess) && Number(f[6], 0, 100, out double effectiveness) &&
                Number(f[7], 0.000001, 1000000, out double knightDamage) && Number(f[8], 0.000001, 1000000, out double knightToughness))
            {
                string key = f[3] + ":" + f[4];
                if (!identities.Add("knight:" + key))
                {
                    result.Knights.Remove(key); reason = "Duplicate knight rejected: " + key; return false;
                }
                result.Knights.Add(key, new KnightStats { Side = f[3], CharacterId = f[4], Prowess = prowess, Effectiveness = effectiveness, DamagePerProwess = knightDamage, ToughnessPerProwess = knightToughness });
                return true;
            }
            return false;
        }

        private static bool Side(string value) => value == "attacker" || value == "defender";
        private static bool Identifier(string value) => value.Length > 0 && value.Length <= 128 && value != "unknown" &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_' || c == '-' || c == '.' || c == ':');
        private static bool Number(string value, double min, double max, out double parsed) =>
            double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed) &&
            !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed >= min && parsed <= max;
    }

    public static class StatScaling
    {
        public static double RequireMultiplier(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(nameof(value), "A finite multiplier within configured bounds is required; no clamping is performed.");
            return value;
        }

        public static bool TryCommanderMultiplier(StatExport export, string side, out double multiplier)
        {
            multiplier = 1;
            if (!export.Complete || !export.Commanders.TryGetValue("attacker", out var attacker) ||
                !export.Commanders.TryGetValue("defender", out var defender) ||
                attacker.DamagePerAdvantage != defender.DamagePerAdvantage || (side != "attacker" && side != "defender")) return false;
            double difference = attacker.Advantage - defender.Advantage;
            if (side == "defender") difference = -difference;
            multiplier = 1 + Math.Max(0, difference) * attacker.DamagePerAdvantage;
            return true;
        }

        public static (double damage, double hitPoints) Regiment(RegimentStats stats, double commanderMultiplier)
        {
            if (stats.BaseDamage <= 0 || stats.BaseToughness <= 0)
                throw new InvalidOperationException("Zero base damage/toughness cannot define a combat-unit scaling ratio.");
            return (stats.Damage / stats.BaseDamage * commanderMultiplier, stats.Toughness / stats.BaseToughness);
        }

        public static int SourceSurvivors(int originalSoldiers, long deployed, long remaining)
        {
            if (originalSoldiers < 0 || deployed <= 0 || remaining < 0)
                throw new ArgumentOutOfRangeException(nameof(deployed), "Exact nonnegative source casualty data is required.");
            // Scaling can round one survivor upward; never create source soldiers.
            return Math.Min(originalSoldiers, (int)Math.Round(originalSoldiers * Math.Min(1.0, (double)remaining / deployed)));
        }
    }
}
