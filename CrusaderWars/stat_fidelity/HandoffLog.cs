using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CrusaderWars.StatFidelity
{
    public sealed record SelectedHandoff(string Text, bool IsFramed, IReadOnlyDictionary<string, string> Fields);

    /// <summary>Bounds both legacy context and CCSTAT to the latest explicitly begun handoff.</summary>
    public static class HandoffLog
    {
        public const string BeginMarker = "CCHANDOFF|v1|BEGIN";
        public const string EndMarker = "CCHANDOFF|v1|END";
        private const int MaximumFrameCharacters = 32 * 1024 * 1024;
        private static readonly string[] Required = {
            "BattleType", "DateYear", "DateMonth", "BattleName", "PlayerCharacter_ID", "PlayerCharacter_Culture",
            "LeftSide_Owner_ID", "LeftSide_Owner_Culture", "RightSide_Owner_ID", "RightSide_Owner_Culture",
            "LeftSide_ID", "RightSide_ID", "LeftSide_Commander_Culture", "RightSide_Commander_Culture",
            "LeftSide_Name", "RightSide_Name", "LeftSide_Rank", "RightSide_Rank", "LeftSide_Prowess", "RightSide_Prowess",
            "Region", "ProvinceID", "SpecialBuilding", "WinterSeverity"
        };
        private static readonly string[] Numeric = {
            "DateYear", "DateMonth", "PlayerCharacter_ID", "PlayerCharacter_Culture", "LeftSide_Owner_ID", "LeftSide_Owner_Culture",
            "RightSide_Owner_ID", "RightSide_Owner_Culture", "LeftSide_ID", "RightSide_ID", "RightSide_Commander_Culture",
            "LeftSide_Rank", "RightSide_Rank", "LeftSide_Prowess", "RightSide_Prowess"
        };
        private static readonly string[] SectionMarkers = {
            "---------Left-Side-Army---------", "---------Right-Side-Army---------", "---------Completed---------", "---------Modifiers---------"
        };

        public static SelectedHandoff SelectLatest(string log, bool requireFramed)
        {
            if (log == null) throw Error("The handoff log is missing.");
            StringBuilder? current = null;
            bool seenBoundary = false, complete = false;
            string? invalid = null;
            using var reader = new StringReader(log);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string marker = line.Trim();
                if (marker.StartsWith("CCHANDOFF|", StringComparison.Ordinal))
                {
                    seenBoundary = true;
                    string[] fields = marker.Split('|');
                    bool begins = fields.Length >= 3 && fields[2] == "BEGIN";
                    if (begins)
                    {
                        current = new StringBuilder(); complete = false;
                        invalid = marker == BeginMarker ? null : "The latest handoff has an unsupported or malformed BEGIN marker.";
                    }
                    else if (marker == EndMarker && current != null && !complete)
                        complete = true;
                    else invalid = "The latest handoff has an orphaned, repeated or unsupported boundary marker.";
                }
                if (current != null && (!complete || marker == EndMarker))
                {
                    if (current.Length + line.Length + 1 > MaximumFrameCharacters)
                        invalid = "The latest handoff exceeds the 32 MiB frame limit.";
                    else current.AppendLine(line);
                }
            }
            if (!seenBoundary)
            {
                if (requireFramed) throw Error("CK3 stat conversion requires a current CCHANDOFF v1 BEGIN/END group. Unframed history cannot supply earlier battle fields.");
                return new SelectedHandoff(log, false, new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
            }
            if (invalid != null) throw Error(invalid);
            if (current == null || !complete) throw Error("The latest begun handoff is incomplete. An older completed handoff was not reused.");
            string selected = current.ToString();
            return new SelectedHandoff(selected, true, Validate(selected));
        }

        private static IReadOnlyDictionary<string, string> Validate(string text)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var needed = Required.Concat(new[] { "TerrainKey", "GarrisonSize", "GarrisonCulture", "HoldingLevel", "Diseases", "Supplies", "Walls", "FortLevel" }).ToHashSet(StringComparer.Ordinal);
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                int delimiter = line.IndexOf(':');
                if (delimiter <= 0) continue;
                string key = line[..delimiter].Trim();
                if (!needed.Contains(key)) continue;
                if (!fields.TryAdd(key, line[(delimiter + 1)..].Trim()))
                    throw Error("The current handoff repeats field '" + key + "'; no first/older value was selected.");
            }
            string[] missing = Required.Where(k => !fields.TryGetValue(k, out string? value) || string.IsNullOrWhiteSpace(value)).ToArray();
            if (missing.Length > 0) throw Error("The current handoff is missing required fields: " + string.Join(", ", missing) + ".");
            if (fields["BattleType"] is not ("BATTLE" or "SIEGE")) throw Error("Unsupported current BattleType.");
            foreach (string key in Numeric)
                if (!Digits(fields[key])) throw Error("The current handoff has an invalid numeric " + key + ".");
            if (!int.TryParse(fields["DateYear"], out int year) || year < 1 ||
                !int.TryParse(fields["DateMonth"], out int month) || month < 1 || month > 12)
                throw Error("The current handoff has an invalid date; no default era was substituted.");
            if (fields["BattleType"] == "BATTLE")
            {
                if (!Digits(fields["ProvinceID"]) || !Digits(fields["LeftSide_Commander_Culture"]))
                    throw Error("The current field battle has an invalid province/culture identity.");
            }
            else
            {
                foreach (string key in new[] { "GarrisonSize", "GarrisonCulture", "HoldingLevel", "Diseases", "Supplies", "Walls", "FortLevel" })
                    if (!fields.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)) throw Error("The current siege handoff is missing " + key + ".");
            }
            foreach (string marker in SectionMarkers)
                if (text.Split(marker, StringSplitOptions.None).Length != 2) throw Error("The current handoff needs exactly one " + marker + " section.");
            if (fields.TryGetValue("TerrainKey", out string? terrain))
            {
                if (!Regex.IsMatch(terrain, "^[a-z_]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    throw Error("The current handoff has an invalid TerrainKey.");
            }
            else if (!Regex.IsMatch(text, @"TOOLTIP:TERRAIN,?[a-z_]+\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw Error("The current handoff has no canonical terrain key.");
            return new ReadOnlyDictionary<string, string>(fields);
        }

        private static bool Digits(string value) => value.Length > 0 && value.Length <= 20 && value.All(c => c >= '0' && c <= '9');
        private static InvalidDataException Error(string message) => new(message + " Run Fight again with the matching CK3 component; historical fields are not a fallback.");
    }
}
