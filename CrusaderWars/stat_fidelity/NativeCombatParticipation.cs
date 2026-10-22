using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity;

public sealed record NativeCombatRegiment(string Side, string RegimentId, decimal? Starting, decimal? Current, decimal? SoftCasualties);
public sealed record FieldSiegeParticipation(string Side, string ArmyId, string ArmyRegimentId, string SourceRegimentId,
    string ChunkIndex, int NativeStock, int ActiveMachines, int ReservedMachines, decimal CombatCurrent,
    string? NativeType = null, string? NativeCategory = null, bool? FightsInMainPhase = null, string? DefinitionSource = null);
public sealed record NativeMaaDefinition(string Key, string? Category, bool? FightsInMainPhase, string SourceFile)
{
    public bool IsSiegeEquipment => Category == "siege_weapon";
    public bool IsFieldNoncombatant => FightsInMainPhase == false;
}

/// <summary>Selected-combat participation, distinct from global surviving equipment stock.</summary>
public sealed class NativeCombatParticipation
{
    private readonly Dictionary<string, NativeCombatRegiment> regiments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> sideArmies = new(StringComparer.Ordinal);

    public NativeCombatRegiment Require(string side, string armyId, string regimentId)
    {
        if (!sideArmies.TryGetValue(side, out var armies) || !armies.Contains(armyId) ||
            !regiments.TryGetValue(side + ":" + regimentId, out var record))
            throw Invalid("Missing exact selected-combat participation for army " + armyId + ", regiment " + regimentId + ".");
        return record;
    }

    public int[] AllocateEquipment(string side, string armyId, string regimentId, IReadOnlyList<int> sourceStocks)
    {
        var record = Require(side, armyId, regimentId);
        if (!record.Current.HasValue)
            throw Invalid("Siege participation has no explicit native current field for regiment " + regimentId + "; absence is not assumed to mean zero.");
        decimal current = record.Current.Value;
        if (record.Starting.HasValue && current > record.Starting.Value)
            throw Invalid("Native siege current exceeds its historical starting count for regiment " + regimentId + ".");
        if (current != decimal.Truncate(current))
            throw Invalid("Fractional field-combat siege participation is not calibrated for whole equipment: regiment " + regimentId + ". Ordinary troop fractions are unaffected.");
        if (current > int.MaxValue)
            throw Invalid("Native field-combat equipment count exceeds the supported range.");
        return SurvivorApportionment.Allocate(sourceStocks, (long)current);
    }

    public static NativeCombatParticipation Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16 * 1024 * 1024)
            throw Invalid("Selected combat is missing or exceeds the bounded participation input limit.");
        var parser = new Parser(text);
        var root = parser.Entries(0, text.Length);
        // ReadPlayerCombat supplies a single numbered combat block. Tests and
        // callers may also supply its already-isolated bare body or braces.
        if (root.Count == 1 && root[0].Value.Block && (root[0].Key == null || Digits(root[0].Key)))
            root = parser.Children(root[0].Value);
        var result = new NativeCombatParticipation();
        foreach (string side in new[] { "attacker", "defender" })
        {
            var sideValue = Single(root, side)?.Value ?? throw Invalid("Selected combat lacks " + side + " participation.");
            if (!sideValue.Block) throw Invalid("Combat side must be a block.");
            var fields = parser.Children(sideValue);
            var armiesValue = Single(fields, "armies")?.Value ?? throw Invalid("Combat side lacks native army identities.");
            if (!armiesValue.Block) throw Invalid("Combat army identities must be an array.");
            var armies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in parser.Children(armiesValue))
            {
                string id = parser.Scalar(entry.Value);
                if (entry.Key != null || !Digits(id) || !armies.Add(id)) throw Invalid("Invalid or duplicate native combat army identity.");
            }
            result.sideArmies.Add(side, armies);
            foreach (string kind in new[] { "men_at_arms", "levies" })
            {
                var array = Single(fields, kind)?.Value;
                if (array == null) continue;
                if (!array.Block) throw Invalid("Combat regiment list must be an array.");
                foreach (var entry in parser.Children(array))
                {
                    if (entry.Key != null || !entry.Value.Block) throw Invalid("Invalid native combat regiment record.");
                    var row = parser.Children(entry.Value);
                    var idValue = Single(row, "regiment")?.Value ?? throw Invalid("Combat regiment has no explicit identity.");
                    string id = parser.Scalar(idValue);
                    if (!Digits(id)) throw Invalid("Invalid native combat regiment identity.");
                    decimal? Number(string name)
                    {
                        var value = Single(row, name)?.Value;
                        if (value == null) return null;
                        string raw = parser.Scalar(value);
                        if (raw.Length > 64 || raw.Length == 0 || raw.Any(c => !char.IsAsciiDigit(c) && c != '.') ||
                            !decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal count) || count < 0)
                            throw Invalid("Invalid native combat count " + name + " for regiment " + id + ".");
                        return count;
                    }
                    var record = new NativeCombatRegiment(side, id, Number("starting"), Number("current"), Number("soft_casualties"));
                    if (result.regiments.Count >= 20000 || !result.regiments.TryAdd(side + ":" + id, record))
                        throw Invalid("Duplicate or excessive combat participation records.");
                }
            }
        }
        return result;
    }

    internal static IReadOnlyList<NativeMaaDefinition> ReadMaaDefinitions(string text, string sourceFile)
    {
        if (text.Length > 16 * 1024 * 1024) throw Invalid("Native MAA definition file exceeds bounds.");
        var parser = new Parser(text);
        var result = new List<NativeMaaDefinition>();
        foreach (var entry in parser.Entries(0, text.Length))
        {
            if (entry.Key == null || entry.Key.StartsWith('@') || !entry.Value.Block) continue;
            var fields = parser.Children(entry.Value);
            string? Text(string key) => Single(fields, key) is Entry value ? Unquote(parser.Scalar(value.Value)) : null;
            string? fights = Text("fights_in_main_phase");
            if (fights is not null and not "yes" and not "no")
                throw Invalid("Unsupported nonliteral native fights_in_main_phase in " + entry.Key + ".");
            result.Add(new(entry.Key, Text("type"), fights == null ? null : fights == "yes", sourceFile));
        }
        return result;
    }

    internal static (string? Path, string[] ReplacePaths) ReadModDescriptor(string text)
    {
        if (text.Length > 1024 * 1024) throw Invalid("Native mod descriptor exceeds bounds.");
        var parser = new Parser(text); var fields = parser.Entries(0, text.Length);
        string? path = Single(fields, "path") is Entry value ? Unquote(parser.Scalar(value.Value)) : null;
        return (path, fields.Where(e => e.Key == "replace_path").Select(e => Unquote(parser.Scalar(e.Value))).ToArray());
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
        ? value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : value;

    private static bool Digits(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 20 && value.All(char.IsAsciiDigit);
    private static InvalidDataException Invalid(string message) => new(message);
    private sealed record Value(int Start, int End, bool Block);
    private sealed record Entry(string? Key, Value Value);
    private static Entry? Single(List<Entry> entries, string key)
    {
        var matches = entries.Where(e => e.Key == key).ToArray();
        if (matches.Length > 1) throw Invalid("Duplicate native combat field: " + key);
        return matches.SingleOrDefault();
    }
    private sealed class Parser(string text)
    {
        public string Scalar(Value value) => !value.Block ? text[value.Start..value.End] : throw Invalid("Expected native scalar, not a block.");
        public List<Entry> Children(Value value) => Entries(value.Start + 1, value.End - 1);
        public List<Entry> Entries(int start, int end)
        {
            var entries = new List<Entry>();
            int cursor = start;
            while ((cursor = Skip(cursor, end)) < end)
            {
                if (entries.Count >= 100000) throw Invalid("Native combat field count exceeds bounds.");
                var first = Read(cursor, end); cursor = Skip(first.End, end);
                if (!first.Block && cursor < end && text[cursor] == '=')
                {
                    var value = Read(Skip(cursor + 1, end), end);
                    entries.Add(new(Scalar(first), value)); cursor = value.End;
                }
                else entries.Add(new(null, first));
            }
            return entries;
        }
        private int Skip(int cursor, int end)
        {
            while (cursor < end)
            {
                if (char.IsWhiteSpace(text[cursor]) || text[cursor] == '\uFEFF') { cursor++; continue; }
                if (text[cursor] != '#') break;
                while (cursor < end && text[cursor] is not '\r' and not '\n') cursor++;
            }
            return cursor;
        }
        private int QuotedEnd(int start, int end)
        {
            for (int i = start + 1; i < end; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i + 1;
            }
            throw Invalid("Unterminated quoted string in combat data.");
        }
        private Value Read(int start, int end)
        {
            if (start >= end || text[start] is '}' or '=') throw Invalid("Missing or invalid native combat value.");
            if (text[start] == '"') return new(start, QuotedEnd(start, end), false);
            if (text[start] != '{')
            {
                int stop = start;
                while (stop < end && !char.IsWhiteSpace(text[stop]) && text[stop] is not '\uFEFF' and not '#' and not '{' and not '}' and not '=') stop++;
                if (stop == start) throw Invalid("Invalid native combat token.");
                return new(start, stop, false);
            }
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (text[i] == '"') { i = QuotedEnd(i, end) - 1; continue; }
                if (text[i] == '#') { while (i < end && text[i] is not '\r' and not '\n') i++; continue; }
                if (text[i] == '{' && ++depth > 96) throw Invalid("Native combat nesting exceeds bounds.");
                if (text[i] == '}' && --depth == 0) return new(start, i + 1, true);
            }
            throw Invalid("Unterminated native combat block.");
        }
    }
}
