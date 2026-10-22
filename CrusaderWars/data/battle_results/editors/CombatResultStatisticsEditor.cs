using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CrusaderWars.data.battle_results.editors;

/// <summary>
/// Exactly one native result group: levy (both identities null), MAA type, or knight ID.
/// FinalCount is the intended survivor target for loss accounting and side totals;
/// it must not prefill CK3's pending per-group completion accumulator.
/// </summary>
public sealed record CombatResultStatisticsUpdate(string Side, string? RegimentType, string? KnightId,
    int MainKills, int PursuitKills, int FinalCount, int PursuitLosses);

/// <summary>
/// Replaces only statistics for an exact existing combat result. CK3 retains
/// ownership of completion, war history, rewards, dates and summary winner fields.
/// </summary>
public static class CombatResultStatisticsEditor
{
    private const int MaximumCharacters = 128 * 1024 * 1024;
    private const int MaximumUpdates = 100_000;
    private const int MaximumDepth = 128;
    private const string NoKnight = "4294967295";
    private readonly record struct GroupKey(string Side, string? RegimentType, string? KnightId);
    private sealed record Value(int Start, int End, bool Block);
    private sealed record Entry(string? Key, Value Value);
    private sealed record Change(int Start, int End, string Replacement);

    /// <param name="sourceText">The complete combat_results={...} section, or its numbered contents.</param>
    public static string Edit(string sourceText, string resultId, IEnumerable<CombatResultStatisticsUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(updates);
        if (!Digits(resultId)) throw Invalid("Invalid exact combat result ID.");
        if (sourceText.Length > MaximumCharacters) throw Invalid("Combat-results section exceeds the bounded editor size.");
        var requested = updates.Take(MaximumUpdates + 1).ToArray();
        if (requested.Length == 0 || requested.Length > MaximumUpdates) throw Invalid("Missing or oversized combat statistics update set.");
        var byGroup = new Dictionary<GroupKey, CombatResultStatisticsUpdate>();
        foreach (var update in requested)
        {
            if (update == null || update.MainKills < 0 || update.PursuitKills < 0 || update.FinalCount < 0 || update.PursuitLosses < 0)
                throw Invalid("Combat statistics must be nonnegative.");
            var key = Key(update.Side, update.RegimentType, update.KnightId);
            if (!byGroup.TryAdd(key, update)) throw Invalid("Duplicate requested combat result group: " + key);
        }

        var parser = new Parser(sourceText);
        var records = parser.Entries(0, sourceText.Length);
        if (records.Count == 1 && records[0].Key == "combat_results" && records[0].Value.Block)
            records = parser.Children(records[0].Value);
        if (records.Any(e => !Digits(e.Key) || (!e.Value.Block && parser.Raw(e.Value) != "none")))
            throw Invalid("Expected numbered combat-result blocks or native none tombstones.");
        var matching = records.Where(e => e.Key == resultId).ToArray();
        if (matching.Length != 1 || !matching[0].Value.Block) throw Invalid("Selected combat result is absent, deleted or ambiguous.");
        var resultFields = parser.Children(matching[0].Value);
        var seen = new HashSet<GroupKey>();
        var changes = new List<Change>();

        foreach (string side in new[] { "attacker", "defender" })
        {
            var sideEntry = Required(resultFields, side);
            if (!sideEntry.Value.Block) throw Invalid("Combat result side must be a block: " + side);
            var sideFields = parser.Children(sideEntry.Value);
            var stats = Required(sideFields, "regiment_stats");
            if (!stats.Value.Block) throw Invalid("Combat regiment statistics must be an array.");
            var groups = parser.Children(stats.Value);
            if (groups.Any(e => e.Key != null || !e.Value.Block)) throw Invalid("Invalid native combat statistics array.");
            long survivors = 0;
            foreach (var group in groups)
            {
                var fields = parser.Children(group.Value);
                var type = Single(fields, "type");
                string? regimentType = type == null ? null : parser.StringValue(type.Value);
                string knight = parser.StringValue(Required(fields, "knight").Value);
                if (!Digits(knight)) throw Invalid("Invalid native knight identity in combat result.");
                var key = Key(side, regimentType, knight == NoKnight ? null : knight);
                if (!seen.Add(key)) throw Invalid("Duplicate native combat result group: " + key);
                if (!byGroup.TryGetValue(key, out var update)) throw Invalid("No exact update for native combat result group: " + key);

                decimal initial = Count(parser.Raw(Required(fields, "initial_count").Value));
                // Verified by a native paused/load/one-day/resave comparison:
                // CK3 adds current survivors to final_count when it completes the
                // combat. Keep its original zero, or survivors are counted twice.
                if (Count(parser.Raw(Required(fields, "final_count").Value)) != 0)
                    throw Invalid("Selected combat result already has a populated completion accumulator: " + key);
                decimal mainLosses = initial - update.FinalCount - update.PursuitLosses;
                if (mainLosses < 0) throw Invalid("Final survivors and pursuit losses exceed the original count: " + key);
                Replace(fields, "main_kills", update.MainKills);
                Replace(fields, "pursuit_kills", update.PursuitKills);
                Replace(fields, "main_losses", mainLosses);
                // AAR supplies total observed pursuit losses, not their CK3 damage cause.
                // Store generic own losses in self; do not invent MAA-caused attribution.
                // This partition is a reporting convention, not independently calibrated
                // native cause attribution. The two fields still sum to the observed loss.
                Replace(fields, "pursuit_losses_maa", 0);
                Replace(fields, "pursuit_losses_self", update.PursuitLosses);
                survivors = checked(survivors + update.FinalCount);
            }
            Replace(sideFields, "surviving_soldiers", survivors);
        }
        if (seen.Count != byGroup.Count) throw Invalid("Update set contains an unknown combat result group.");

        var edited = new StringBuilder(sourceText);
        int previousStart = sourceText.Length;
        foreach (var change in changes.OrderByDescending(c => c.Start))
        {
            if (change.End > previousStart) throw Invalid("Overlapping combat result edits.");
            edited.Remove(change.Start, change.End - change.Start).Insert(change.Start, change.Replacement);
            previousStart = change.Start;
        }
        return edited.ToString();

        void Replace(List<Entry> fields, string name, decimal number)
        {
            var target = Required(fields, name).Value;
            Count(parser.Raw(target));
            changes.Add(new Change(target.Start, target.End, number.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static GroupKey Key(string side, string? type, string? knight)
    {
        if (side is not ("attacker" or "defender") || (type != null && (type.Length == 0 || type.Length > 512 || type.Any(char.IsControl))) ||
            (knight != null && (!Digits(knight) || knight == NoKnight)) || (type != null && knight != null))
            throw Invalid("Unknown or ambiguous combat group identity.");
        return new GroupKey(side, type, knight);
    }
    private static Entry Required(List<Entry> fields, string name) => Single(fields, name) ?? throw Invalid("Missing native combat field: " + name);
    private static Entry? Single(List<Entry> fields, string name)
    {
        var matches = fields.Where(e => e.Key == name).ToArray();
        if (matches.Length > 1) throw Invalid("Duplicate native combat field: " + name);
        return matches.SingleOrDefault();
    }
    private static bool Digits(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 20 && value.All(c => c >= '0' && c <= '9');
    private static decimal Count(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(c => (c < '0' || c > '9') && c != '.') ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal count) || count < 0)
            throw Invalid("Invalid native combat statistics count: " + value);
        return count;
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
