using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CrusaderWars.StatFidelity;

/// <summary>Preserves RPFM's schema/version metadata and its exported column order.</summary>
public sealed class TsvTable
{
    public string Name { get; }
    public string Version { get; }
    public string[] Columns { get; }
    public List<Dictionary<string, string>> Rows { get; } = new();

    public TsvTable(string name, string version, string[] columns)
    {
        Name = name; Version = version; Columns = columns;
        if (Columns.Distinct(StringComparer.Ordinal).Count() != Columns.Length)
            throw new InvalidDataException($"Duplicate columns in {name}.");
    }

    public static TsvTable Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var records = Parse(reader).ToList();
        if (records.Count < 2) throw new InvalidDataException($"Missing RPFM TSV header: {path}");
        var metadata = records[1][0].TrimStart('\uFEFF').Split(';');
        if (metadata.Length < 2 || !metadata[0].StartsWith('#'))
            throw new InvalidDataException($"Invalid RPFM TSV metadata: {path}");
        var table = new TsvTable(metadata[0][1..], metadata[1], records[0]);
        foreach (var record in records.Skip(2))
        {
            if (record.Length == 1 && record[0].Length == 0) continue;
            if (record.Length != table.Columns.Length)
                throw new InvalidDataException($"Column count mismatch in {path}: {record.Length} instead of {table.Columns.Length}.");
            table.Rows.Add(table.Columns.Select((column, i) => (column, record[i]))
                .ToDictionary(x => x.column, x => x.Item2, StringComparer.Ordinal));
        }
        return table;
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.NewLine = "\n";
        writer.WriteLine(string.Join('\t', Columns.Select(Quote)));
        var metadata = Enumerable.Repeat("", Columns.Length).ToArray();
        string virtualPath = Name == "Loc" ? "text/db" : $"db/{Name}";
        metadata[0] = $"#{Name};{Version};{virtualPath}/{Path.GetFileNameWithoutExtension(path)}";
        writer.WriteLine(string.Join('\t', metadata.Select(Quote)));
        foreach (var row in Rows)
            writer.WriteLine(string.Join('\t', Columns.Select(c => Quote(row[c]))));
    }

    private static string Quote(string value) => value.IndexOfAny(new[] { '\t', '\n', '\r', '"' }) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static IEnumerable<string[]> Parse(TextReader reader)
    {
        var row = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        int next;
        while ((next = reader.Read()) >= 0)
        {
            char c = (char)next;
            if (c == '"' && (quoted || field.Length == 0))
            {
                if (quoted && reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                else quoted = !quoted;
            }
            // RPFM writes LF-delimited exports and can leave a literal lone CR
            // inside an unquoted DB string (observed in Earl's infantry pack).
            // Preserve that value; only LF or CRLF terminates a record.
            else if (!quoted && (c == '\t' || c == '\n' || (c == '\r' && reader.Peek() == '\n')))
            {
                row.Add(field.ToString()); field.Clear();
                if (c != '\t')
                {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    yield return row.ToArray(); row.Clear();
                }
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed quoted TSV field.");
        if (row.Count > 0 || field.Length > 0) { row.Add(field.ToString()); yield return row.ToArray(); }
    }
}
