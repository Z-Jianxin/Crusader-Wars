using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity;

/// <summary>Merged active tables. Inputs are supplied from lowest to highest priority.</summary>
public sealed class AttilaDatabase
{
    internal static readonly Dictionary<string, string[]> Keys = new(StringComparer.Ordinal)
    {
        ["main_units_tables"] = new[] { "unit" },
        ["land_units_tables"] = new[] { "key" },
        ["melee_weapons_tables"] = new[] { "key" },
        ["missile_weapons_tables"] = new[] { "key" },
        ["projectiles_tables"] = new[] { "key" },
        ["battle_entities_tables"] = new[] { "key" },
        ["mounts_tables"] = new[] { "key" },
        ["battlefield_engines_tables"] = new[] { "key" },
        ["unit_variants_tables"] = new[] { "unit", "faction" },
        ["land_units_to_unit_abilites_junctions_tables"] = new[] { "land_unit", "ability" },
        ["missile_weapons_to_projectiles_tables"] = new[] { "missile_weapon", "projectile" },
        ["Loc"] = new[] { "key" }
    };
    internal readonly Dictionary<string, TsvTable> Tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> rowIndices = new(StringComparer.Ordinal);

    public void Merge(TsvTable incoming)
    {
        if (!Keys.TryGetValue(incoming.Name, out var keys)) return;
        if (keys.Any(k => !incoming.Columns.Contains(k)))
        {
            throw new InvalidDataException($"Cannot identify {incoming.Name} rows: expected {string.Join(", ", keys)}.");
        }
        if (!Tables.TryGetValue(incoming.Name, out var table))
        {
            Tables[incoming.Name] = table = new TsvTable(incoming.Name, incoming.Version, incoming.Columns);
            rowIndices[incoming.Name] = new(StringComparer.Ordinal);
        }
        if (table.Version != incoming.Version || !table.Columns.SequenceEqual(incoming.Columns))
            throw new InvalidDataException($"Incompatible active schemas for {incoming.Name}; re-export with the bundled Attila schema.");
        var indices = rowIndices[incoming.Name];
        foreach (var row in incoming.Rows)
        {
            string identity = Identity(row, keys);
            var copy = new Dictionary<string, string>(row, StringComparer.Ordinal);
            if (indices.TryGetValue(identity, out int index)) table.Rows[index] = copy;
            else { indices.Add(identity, table.Rows.Count); table.Rows.Add(copy); }
        }
    }

    public void MergeDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        // Total War's alphabetically earlier DB fragments have priority within a pack.
        foreach (string path in Directory.GetFiles(directory, "*.tsv", SearchOption.AllDirectories)
                     .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
            Merge(TsvTable.Read(path));
    }

    /// <summary>Resolve virtual-file replacement before merging rows: an override can remove old rows.</summary>
    public static AttilaDatabase FromPackDirectories(IReadOnlyList<string> directoriesLowToHigh)
    {
        var winners = new Dictionary<string, (string file, int priority)>(StringComparer.OrdinalIgnoreCase);
        for (int priority = 0; priority < directoriesLowToHigh.Count; priority++)
        {
            string root = directoriesLowToHigh[priority];
            foreach (string file in Directory.GetFiles(root, "*.tsv", SearchOption.AllDirectories))
            {
                string virtualPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                // RPFM appends .tsv to the original virtual filename on export, including .loc.tsv.
                virtualPath = virtualPath[..^4];
                winners[virtualPath] = (file, priority);
            }
        }
        var database = new AttilaDatabase();
        foreach (var item in winners.OrderBy(x => x.Value.priority)
                     .ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase))
            database.Merge(TsvTable.Read(item.Value.file));
        return database;
    }

    internal Dictionary<string, string> Require(string table, string key)
    {
        var result = rowIndices.TryGetValue(table, out var indices) && indices.TryGetValue(key, out int index)
            ? Tables[table].Rows[index] : null;
        return result ?? throw new InvalidOperationException($"Stat fidelity cannot resolve {table} '{key}' in the active packs. Restore the required Attila/mod files or disable stat fidelity.");
    }

    internal Dictionary<string, string>? Lookup(string table, string key) =>
        rowIndices.TryGetValue(table, out var indices) && indices.TryGetValue(key, out int index) ? Tables[table].Rows[index] : null;

    internal IEnumerable<Dictionary<string, string>> Find(string table, string column, string value) =>
        Tables.TryGetValue(table, out var data) ? data.Rows.Where(r => r.TryGetValue(column, out var cell) && cell == value) : Enumerable.Empty<Dictionary<string, string>>();

    private static string Identity(Dictionary<string, string> row, IEnumerable<string> keys) => string.Join('\u001f', keys.Select(k => row[k]));
}
