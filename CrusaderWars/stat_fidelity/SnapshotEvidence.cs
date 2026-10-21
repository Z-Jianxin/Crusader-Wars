using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CrusaderWars.StatFidelity
{
    /// <summary>Matches log evidence to the selected player's freshly saved combat snapshot.</summary>
    public static class SnapshotEvidence
    {
        private const int MaximumCharacterBlock = 2 * 1024 * 1024;
        // Verified against a current 1.20.1.2 save: set_variable value=1 -> identity=100000.
        public const long SavedValueScale = 100000;

        public static bool Validate(TextReader living, string playerId, string snapshotId, string combatId, out string reason)
        {
            reason = "Snapshot freshness rejected: missing or invalid player, combat or snapshot identity.";
            if (!Digits(playerId) || !Digits(combatId) || !snapshotId.StartsWith("snapshot-", StringComparison.Ordinal) ||
                !long.TryParse(snapshotId.Substring(9), NumberStyles.None, CultureInfo.InvariantCulture, out long serial) || serial <= 0 || serial > long.MaxValue / SavedValueScale)
                return false;
            string? block = ReadCharacter(living, playerId);
            if (block == null)
            {
                reason = "Snapshot freshness rejected: selected player's bounded character block is missing or incomplete.";
                return false;
            }
            if (!TryVariable(block, "ccstat_snapshot_serial", "value", out string rawSerial) ||
                !long.TryParse(rawSerial, NumberStyles.None, CultureInfo.InvariantCulture, out long savedSerial) || savedSerial != serial * SavedValueScale)
            {
                reason = "Snapshot freshness rejected: exported serial does not match the selected player's handoff save.";
                return false;
            }
            if (!TryVariable(block, "ccstat_combat", "combat", out string savedCombat) || savedCombat != combatId)
            {
                reason = "Snapshot freshness rejected: saved ccstat_combat does not identify the selected combat.";
                return false;
            }
            reason = "Snapshot serial and selected combat match the handoff save.";
            return true;
        }

        private static string? ReadCharacter(TextReader living, string playerId)
        {
            int depth = 0, startDepth = 0;
            StringBuilder? block = null;
            string? line;
            while ((line = living.ReadLine()) != null)
            {
                if (line.Length > MaximumCharacterBlock) return null;
                if (block == null && depth <= 1 && Regex.IsMatch(line, @"^\s*" + Regex.Escape(playerId) + @"\s*=\s*\{\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    startDepth = depth;
                    block = new StringBuilder();
                }
                if (block != null)
                {
                    block.AppendLine(line);
                    if (block.Length > MaximumCharacterBlock) return null;
                }
                depth += BraceDelta(line);
                if (depth < 0) return null;
                if (block != null && depth == startDepth) return block.ToString();
            }
            return null;
        }

        private static int BraceDelta(string line)
        {
            bool quoted = false, escaped = false;
            int delta = 0;
            foreach (char c in line)
            {
                if (escaped) { escaped = false; continue; }
                if (quoted && c == '\\') { escaped = true; continue; }
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (c == '#') break;
                if (c == '{') delta++;
                if (c == '}') delta--;
            }
            return delta;
        }

        private static bool TryVariable(string block, string flag, string expectedType, out string identity)
        {
            identity = "";
            var matches = Regex.Matches(block, @"\bflag\s*=\s*""?" + Regex.Escape(flag) + @"""?\s+data\s*=\s*\{(?<data>[^{}]{0,4096})\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (matches.Count != 1) return false;
            string data = matches[0].Groups["data"].Value;
            var type = Regex.Matches(data, @"\btype\s*=\s*([a-z_]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var value = Regex.Matches(data, @"\bidentity\s*=\s*(\d+)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (type.Count != 1 || value.Count != 1 || type[0].Groups[1].Value != expectedType) return false;
            identity = value[0].Groups[1].Value;
            return true;
        }

        private static bool Digits(string value) => !string.IsNullOrEmpty(value) && value.Length <= 20 &&
            Regex.IsMatch(value, @"^\d+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
