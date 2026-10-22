using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CrusaderWars.data.battle_results.editors;

/// <summary>An exact saved source/chunk identity, with a nonnegative invariant count.</summary>
public sealed record SourceRegimentUpdate(string SourceRegimentId, int ChunkIndex, string Current);

/// <summary>
/// Edits the native regiments section without reconstructing existing sources.
/// In particular, levy chunks and their indices belong to other raised armies too.
/// All validation completes before a changed string is returned; this class does no I/O.
/// </summary>
public static class SourceRegimentEditor
{
    private const int MaximumCharacters = 128 * 1024 * 1024;
    private const int MaximumUpdates = 100_000;
    private const int MaximumDepth = 128;
    private sealed record Value(int Start, int End, bool Block);
    private sealed record Entry(string? Key, int KeyStart, int KeyEnd, Value Value);
    private sealed record Change(int Start, int End, string Replacement);

    /// <param name="sourceText">The complete regiments={...} section, or its numbered-record contents.</param>
    public static string Edit(string sourceText, IEnumerable<SourceRegimentUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(updates);
        if (sourceText.Length > MaximumCharacters) throw Invalid("Source section exceeds the bounded editor size.");
        var requested = updates.Take(MaximumUpdates + 1).ToArray();
        if (requested.Length > MaximumUpdates) throw Invalid("Too many source updates.");
        var identities = new HashSet<(string, int)>();
        foreach (var update in requested)
        {
            if (update == null || !Digits(update.SourceRegimentId) || update.ChunkIndex < 0)
                throw Invalid("Invalid exact source/chunk identity.");
            Count(update.Current);
            if (!identities.Add((update.SourceRegimentId, update.ChunkIndex)))
                throw Invalid("Duplicate requested source/chunk: " + update.SourceRegimentId + ":" + update.ChunkIndex);
        }
        if (requested.Length == 0) return sourceText;

        var parser = new Parser(sourceText);
        var records = parser.Entries(0, sourceText.Length);
        if (records.Count == 1 && records[0].Key == "regiments" && records[0].Value.Block)
            records = parser.Children(records[0].Value);
        // Native databases retain numbered `none` tombstones for deleted sources.
        if (records.Any(e => !Digits(e.Key) || (!e.Value.Block && parser.Scalar(e.Value) != "none")))
            throw Invalid("Expected numbered source blocks or native none tombstones.");
        var wanted = requested.GroupBy(u => u.SourceRegimentId).ToDictionary(g => g.Key, g => g.ToArray());
        var sources = new Dictionary<string, Entry>();
        foreach (var record in records)
            if (wanted.ContainsKey(record.Key!) && !sources.TryAdd(record.Key!, record))
                throw Invalid("Ambiguous duplicate source regiment: " + record.Key);

        var changes = new List<Change>();
        foreach (var (id, sourceUpdates) in wanted)
        {
            if (!sources.TryGetValue(id, out var source) || !source.Value.Block)
                throw Invalid("Requested source is absent or deleted: " + id);
            var fields = parser.Children(source.Value);
            var chunks = Single(fields, "chunks");
            var size = Single(fields, "size");
            if (chunks != null && size != null) throw Invalid("Source has both compact size and chunks: " + id);
            if (chunks != null)
            {
                if (!chunks.Value.Block) throw Invalid("Source chunks must be an array: " + id);
                var array = parser.Children(chunks.Value);
                if (array.Any(e => e.Key != null || !e.Value.Block)) throw Invalid("Invalid native chunk array: " + id);
                foreach (var update in sourceUpdates)
                {
                    if (update.ChunkIndex >= array.Count) throw Invalid("Requested chunk is absent: " + id + ":" + update.ChunkIndex);
                    var chunk = array[update.ChunkIndex].Value;
                    var chunkFields = parser.Children(chunk);
                    var max = Single(chunkFields, "max") ?? throw Invalid("Selected chunk has no maximum: " + id);
                    var current = Single(chunkFields, "current");
                    ValidateWithinMaximum(update.Current, parser.Scalar(max.Value), id + ":" + update.ChunkIndex);
                    ValidateWithinMaximum(update.Current, current == null ? "0" : parser.Scalar(current.Value),
                        id + ":" + update.ChunkIndex + " original current");
                    if (current != null)
                    {
                        Count(parser.Scalar(current.Value));
                        changes.Add(new Change(current.Value.Start, current.Value.End, update.Current));
                    }
                    else
                    {
                        // CK3 may omit a default-zero scalar. Keep the block and all other fields intact.
                        changes.Add(new Change(chunk.End - 1, chunk.End - 1, " current=" + update.Current + " "));
                    }
                }
            }
            else
            {
                if (size == null || sourceUpdates.Length != 1 || sourceUpdates[0].ChunkIndex != 0)
                    throw Invalid("Compact sources have exactly one implicit chunk at index zero: " + id);
                string originalSize = parser.Scalar(size.Value);
                ValidateWithinMaximum(sourceUpdates[0].Current, originalSize, id + ":0");
                var rootMax = Single(fields, "max");
                if (rootMax != null) ValidateWithinMaximum(originalSize, parser.Scalar(rootMax.Value));
                string newline = sourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                string indent = parser.Indent(size.KeyStart);
                string expanded = "chunks={" + newline + indent + "\t{" + newline + indent + "\t\tmax=" + originalSize +
                    newline + indent + "\t\tcurrent=" + sourceUpdates[0].Current + newline + indent + "\t}" + newline + indent + "}";
                if (rootMax == null)
                {
                    // Rename only the compact size key, then add its explicit chunk.
                    // Source/type/owner/origin/uses_supply and arbitrary future fields retain their bytes.
                    changes.Add(new Change(size.KeyStart, size.KeyEnd, "max"));
                    changes.Add(new Change(size.Value.End, size.Value.End, newline + indent + expanded));
                }
                else
                    changes.Add(new Change(size.KeyStart, size.Value.End, expanded));
            }
        }

        var result = new StringBuilder(sourceText);
        int previousStart = sourceText.Length;
        foreach (var change in changes.OrderByDescending(c => c.Start).ThenByDescending(c => c.End))
        {
            if (change.End > previousStart) throw Invalid("Overlapping source edits.");
            result.Remove(change.Start, change.End - change.Start).Insert(change.Start, change.Replacement);
            previousStart = change.Start;
        }
        return result.ToString();
    }

    private static Entry? Single(List<Entry> fields, string key)
    {
        var matches = fields.Where(e => e.Key == key).ToArray();
        if (matches.Length > 1) throw Invalid("Ambiguous duplicate field: " + key);
        return matches.SingleOrDefault();
    }

    private static bool Digits(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 20 && value.All(c => c >= '0' && c <= '9');
    private static decimal Count(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(c => (c < '0' || c > '9') && c != '.') ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal count) || count < 0)
            throw Invalid("Invalid invariant regiment count: " + value);
        return count;
    }
    private static void ValidateWithinMaximum(string current, string maximum, string identity = "compact source root")
    {
        if (Count(current) > Count(maximum)) throw Invalid("Source update " + identity + " (" + current + ") exceeds the saved maximum (" + maximum + ").");
    }
    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Parser(string text)
    {
        public string Scalar(Value value)
        {
            if (value.Block) throw Invalid("Expected a count scalar, not a block.");
            return text[value.Start..value.End];
        }
        public List<Entry> Children(Value value) => Entries(value.Start + 1, value.End - 1);
        public string Indent(int start)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            string prefix = text[lineStart..start];
            return prefix.All(c => c == ' ' || c == '\t') ? prefix : "\t\t\t";
        }
        public List<Entry> Entries(int start, int end)
        {
            var entries = new List<Entry>();
            int position = start;
            while ((position = Skip(position, end)) < end)
            {
                int keyStart = position;
                Value first = Read(position, end);
                position = Skip(first.End, end);
                if (!first.Block && position < end && text[position] == '=')
                {
                    string key = text[first.Start..first.End];
                    position = Skip(position + 1, end);
                    Value value = Read(position, end);
                    entries.Add(new Entry(key, keyStart, first.End, value));
                    position = value.End;
                }
                else
                    entries.Add(new Entry(null, keyStart, first.End, first));
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
            throw Invalid("Unterminated quoted string in source section.");
        }
        private Value Read(int start, int end)
        {
            if (start >= end || text[start] == '}' || text[start] == '=') throw Invalid("Missing or invalid source value.");
            if (text[start] == '"') return new Value(start, QuotedEnd(start, end), false);
            if (text[start] != '{')
            {
                int stop = start;
                while (stop < end && !char.IsWhiteSpace(text[stop]) && text[stop] != '\uFEFF' && text[stop] != '#' &&
                    text[stop] != '{' && text[stop] != '}' && text[stop] != '=') stop++;
                if (stop == start) throw Invalid("Invalid source token.");
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
                if (text[i] == '{' && ++depth > MaximumDepth) throw Invalid("Source nesting exceeds the bounded editor depth.");
                if (text[i] == '}' && --depth == 0) return new Value(start, i + 1, true);
            }
            throw Invalid("Unterminated source block.");
        }
    }
}
