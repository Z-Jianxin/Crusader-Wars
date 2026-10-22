using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CrusaderWars.data.battle_results.editors;

/// <summary>Exact original and returned native populations, before CK3 completes the battle.</summary>
public sealed record NativeCombatReturnUpdate(string Side, string RegimentId,
    decimal OriginalActive, decimal OriginalSoft, decimal ActiveRemaining,
    int OriginalStock, int ReturnedStock, int EquipmentReserve = 0, bool CharacterUnavailable = false, bool IsCharacter = false, bool IndependentCommanderRemoval = false);

/// <summary>
/// Edits one isolated native combat without interpreting side bootstrap counters
/// as current active counts. Existing wounded pools remain native state. Only an
/// explicitly unavailable character may clear its personal fractional pool.
/// </summary>
public static class NativeCombatReturnEditor
{
    private const int MaximumDepth = 96;
    private sealed record Value(int Start, int End, bool Block);
    private sealed record Entry(string? Key, Value Value);
    private sealed record Change(int Start, int End, string Replacement);

    public static string Edit(string original, string winningSide, IEnumerable<NativeCombatReturnUpdate> updates, string? expectedCombatId = null)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(updates);
        if (original.Length == 0 || original.Length > 16 * 1024 * 1024 || winningSide is not ("attacker" or "defender"))
            throw Invalid("Invalid selected combat or winning side.");
        var requested = updates.Take(20001).ToArray();
        if (requested.Length > 20000) throw Invalid("Excessive combat return records.");
        var byKey = new Dictionary<(string, string), NativeCombatReturnUpdate>();
        foreach (var update in requested)
        {
            if (update.Side is not ("attacker" or "defender") || !Digits(update.RegimentId) ||
                update.OriginalStock < 0 || update.ReturnedStock < 0 || update.ReturnedStock > update.OriginalStock ||
                update.EquipmentReserve < 0 || update.EquipmentReserve > update.ReturnedStock ||
                !NativePrecision(update.OriginalActive) || !NativePrecision(update.OriginalSoft) || !NativePrecision(update.ActiveRemaining) ||
                update.ActiveRemaining > update.OriginalActive)
                throw Invalid("Invalid or increasing native population return: " + update.RegimentId);
            decimal soft = update.CharacterUnavailable ? 0 : update.OriginalSoft;
            if (update.IndependentCommanderRemoval && (!update.IsCharacter || !update.CharacterUnavailable || update.OriginalActive != 0))
                throw Invalid("Independent commander removal requires a separate tracked commander fate for an inactive personal identity.");
            if (update.CharacterUnavailable && (!update.IsCharacter || update.OriginalStock != 1 ||
                update.OriginalActive == 0 && !update.IndependentCommanderRemoval ||
                update.ReturnedStock != 0 || update.ActiveRemaining != 0 || update.EquipmentReserve != 0))
                throw Invalid("Unavailable-character return must remove one participating native character identity.");
            if (update.IsCharacter)
            {
                // A cached actor identity may remain in the army while explicitly
                // inactive (A=0/S=0). This is not inferred wounded troop strength.
                if (update.OriginalStock > 1 || update.EquipmentReserve != 0 ||
                    update.OriginalActive + update.OriginalSoft > update.OriginalStock ||
                    !update.CharacterUnavailable && (update.ReturnedStock != update.OriginalStock || update.ActiveRemaining != update.OriginalActive))
                    throw Invalid("Native character availability must follow its identity and resolved fate, not retinue body rounding.");
            }
            else if (decimal.Ceiling(update.OriginalActive + update.OriginalSoft) + update.EquipmentReserve != update.OriginalStock ||
                decimal.Ceiling(update.ActiveRemaining + soft) + update.EquipmentReserve != update.ReturnedStock)
                throw Invalid("Native active, wounded, reserve and integer stock disagree: " + update.RegimentId);
            if (!byKey.TryAdd((update.Side, update.RegimentId), update)) throw Invalid("Duplicate native combat return identity.");
        }
        var parser = new Parser(original);
        var roots = parser.Entries(0, original.Length);
        if (roots.Count != 1 || !Digits(roots[0].Key) || !roots[0].Value.Block)
            throw Invalid("Expected one exact numbered native combat block.");
        if (expectedCombatId != null && (!Digits(expectedCombatId) || roots[0].Key != expectedCombatId))
            throw Invalid("Selected combat identity differs from the bound snapshot.");
        var combat = roots[0].Value;
        var fields = parser.Children(combat);
        var changes = new List<Change>();
        var seen = new HashSet<(string, string)>();
        foreach (string side in new[] { "attacker", "defender" })
        {
            var sideEntry = Required(fields, side);
            if (!sideEntry.Value.Block) throw Invalid("Native combat side is not a block.");
            var sideFields = parser.Children(sideEntry.Value);
            foreach (string kind in new[] { "men_at_arms", "levies" })
            {
                var list = Single(sideFields, kind);
                if (list == null) continue;
                if (!list.Value.Block) throw Invalid("Native combat regiment list is not a block.");
                foreach (var entry in parser.Children(list.Value))
                {
                    if (entry.Key != null || !entry.Value.Block) throw Invalid("Native combat regiment list contains an invalid record.");
                    var record = parser.Children(entry.Value);
                    string id = parser.Raw(Required(record, "regiment").Value);
                    var key = (side, id);
                    if (!Digits(id) || !seen.Add(key) || !byKey.TryGetValue(key, out var update))
                        throw Invalid("Missing, duplicate or unknown exact native combat return: " + side + ":" + id);
                    var active = Required(record, "current").Value;
                    var soft = Required(record, "soft_casualties").Value;
                    if (Count(parser.Raw(active)) != update.OriginalActive || Count(parser.Raw(soft)) != update.OriginalSoft)
                        throw Invalid("Native combat changed since its original population snapshot: " + side + ":" + id);
                    Replace(active, update.ActiveRemaining.ToString("0.#####", CultureInfo.InvariantCulture));
                    // Preserve the original soft-casualty spelling and bytes, not merely its value.
                    if (update.CharacterUnavailable) Replace(soft, "0");
                }
            }
        }
        if (seen.Count != byKey.Count) throw Invalid("Return includes a native regiment absent from the selected combat.");
        string phase = parser.Raw(Required(fields, "phase").Value);
        if (phase is not ("main" or "pursuit")) throw Invalid("Native combat is not in a supported pending combat phase.");
        Replace(Required(fields, "phase").Value, "pursuit");
        Count(parser.Raw(Required(fields, "days").Value));
        Replace(Required(fields, "days").Value, "3");
        SetOrInsert("winning_side", winningSide);
        SetOrInsert("wiped", "no");
        var edited = new StringBuilder(original);
        int previous = original.Length;
        foreach (var change in changes.OrderByDescending(c => c.Start))
        {
            if (change.End > previous) throw Invalid("Overlapping native combat return edits.");
            edited.Remove(change.Start, change.End - change.Start).Insert(change.Start, change.Replacement);
            previous = change.Start;
        }
        return edited.ToString();

        void Replace(Value value, string replacement) => changes.Add(new(value.Start, value.End, replacement));
        void SetOrInsert(string key, string value)
        {
            var existing = Single(fields, key);
            if (existing != null) { parser.Raw(existing.Value); Replace(existing.Value, value); }
            else changes.Add(new(combat.End - 1, combat.End - 1, " " + key + "=" + value + " "));
        }
    }

    /// <summary>Replace only the exact selected block in its immutable source section.</summary>
    public static string ReplaceInSection(string section, string combatId, string original, string edited)
    {
        ArgumentNullException.ThrowIfNull(section); ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(edited);
        if (original.Length > 16 * 1024 * 1024 || edited.Length > 16 * 1024 * 1024) throw Invalid("Selected combat exceeds the bounded replacement size.");
        if (section.Length > 128 * 1024 * 1024 || !Digits(combatId)) throw Invalid("Invalid native combat section or identity.");
        string Body(string text)
        {
            var parser = new Parser(text); var entries = parser.Entries(0, text.Length);
            if (entries.Count != 1 || entries[0].Key != combatId || !entries[0].Value.Block)
                throw Invalid("Replacement combat identity differs from the selected native record.");
            return text[entries[0].Value.Start..entries[0].Value.End];
        }
        string originalBody = Body(original), editedBody = Body(edited);
        var sectionParser = new Parser(section); var records = sectionParser.Entries(0, section.Length);
        if (records.Count == 1 && records[0].Key == "combats" && records[0].Value.Block)
            records = sectionParser.Children(records[0].Value);
        if (records.Any(row => !Digits(row.Key) || (!row.Value.Block && sectionParser.Raw(row.Value) != "none")))
            throw Invalid("Native combat section contains invalid numbered records.");
        var selected = records.Where(row => row.Key == combatId).ToArray();
        if (selected.Length != 1 || !selected[0].Value.Block)
            throw Invalid("Exact selected native combat is missing or duplicated.");
        var target = selected[0].Value;
        if (section[target.Start..target.End] != originalBody)
            throw Invalid("Selected native combat differs from its original immutable snapshot.");
        return section[..target.Start] + editedBody + section[target.End..];
    }

    private static bool NativePrecision(decimal value) => value >= 0 && value <= int.MaxValue && value % 0.00001m == 0;
    private static Entry Required(List<Entry> fields, string name) => Single(fields, name) ?? throw Invalid("Missing native combat field: " + name);
    private static Entry? Single(List<Entry> fields, string name)
    {
        var matches = fields.Where(e => e.Key == name).ToArray();
        if (matches.Length > 1) throw Invalid("Duplicate native combat field: " + name);
        return matches.SingleOrDefault();
    }
    private static bool Digits(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 20 && value.All(char.IsAsciiDigit);
    private static decimal Count(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(c => !char.IsAsciiDigit(c) && c != '.') ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) || !NativePrecision(number))
            throw Invalid("Invalid native count or unsupported precision: " + value);
        return number;
    }
    private static InvalidDataException Invalid(string message) => new(message);
    private sealed class Parser(string text)
    {
        public string Raw(Value value)
        {
            if (value.Block) throw Invalid("Expected scalar combat statistic.");
            return text[value.Start..value.End];
        }
        public string StringValue(Value value)
        {
            string raw = Raw(value);
            if (!raw.StartsWith('"')) return raw;
            var result = new StringBuilder();
            for (int i = 1; i < raw.Length - 1; i++)
            {
                if (raw[i] == '\\' && i + 1 < raw.Length - 1 && (raw[i + 1] == '"' || raw[i + 1] == '\\')) i++;
                result.Append(raw[i]);
            }
            return result.ToString();
        }
        public List<Entry> Children(Value value) => Entries(value.Start + 1, value.End - 1);
        public List<Entry> Entries(int start, int end)
        {
            var entries = new List<Entry>();
            int position = start;
            while ((position = Skip(position, end)) < end)
            {
                if (entries.Count >= 100000) throw Invalid("Excessive native combat field count.");
                Value first = Read(position, end);
                position = Skip(first.End, end);
                if (!first.Block && position < end && text[position] == '=')
                {
                    string key = text[first.Start..first.End];
                    Value value = Read(Skip(position + 1, end), end);
                    entries.Add(new Entry(key, value));
                    position = value.End;
                }
                else entries.Add(new Entry(null, first));
            }
            return entries;
        }
        private int Skip(int position, int end)
        {
            while (position < end)
            {
                if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF') { position++; continue; }
                if (text[position] != '#') break;
                while (position < end && text[position] != '\r' && text[position] != '\n') position++;
            }
            return position;
        }
        private int QuotedEnd(int start, int end)
        {
            for (int i = start + 1; i < end; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i + 1;
            }
            throw Invalid("Unterminated quoted combat field.");
        }
        private Value Read(int start, int end)
        {
            if (start >= end || text[start] is '}' or '=') throw Invalid("Missing or invalid combat value.");
            if (text[start] == '"') return new Value(start, QuotedEnd(start, end), false);
            if (text[start] != '{')
            {
                int stop = start;
                while (stop < end && !char.IsWhiteSpace(text[stop]) && text[stop] != '\uFEFF' && text[stop] is not ('#' or '{' or '}' or '=')) stop++;
                if (stop == start) throw Invalid("Invalid combat token.");
                return new Value(start, stop, false);
            }
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (text[i] == '"') { i = QuotedEnd(i, end) - 1; continue; }
                if (text[i] == '#')
                {
                    while (i < end && text[i] != '\r' && text[i] != '\n') i++;
                    continue;
                }
                if (text[i] == '{' && ++depth > MaximumDepth) throw Invalid("Combat nesting exceeds the bounded editor depth.");
                if (text[i] == '}' && --depth == 0) return new Value(start, i + 1, true);
            }
            throw Invalid("Unterminated combat block.");
        }
    }
}
