using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CrusaderWars.StatFidelity;

public static partial class StatFidelityBackend
{
    private const string GeneratedFile = "cc_stat_fidelity.tsv";
    private const string GeneratedLoc = "cc_stat_fidelity.loc.tsv";

    public static IReadOnlyDictionary<string, string> Generate(string runtimeRoot, string attilaExecutable, IReadOnlyCollection<UnitStatProfile> profiles)
    {
        string rpfm = Path.Combine(runtimeRoot, "data", "rpfm", "rpfm_cli.exe");
        string schema = Path.Combine(runtimeRoot, "data", "schema_att.ron");
        if (!File.Exists(schema)) throw new FileNotFoundException("The bundled Attila RPFM schema is missing.", schema);
        var packs = PackSourceResolver.Resolve(attilaExecutable);
        var database = LoadPacks(rpfm, schema, packs, Path.Combine(runtimeRoot, "data", "stat-fidelity-cache"));
        // Per-battle localization already prepared by Crusader Conflicts wins over the original names.
        string battleRoot = Path.Combine(runtimeRoot, "data", "battle files");
        foreach (string path in Directory.GetFiles(battleRoot, "*.loc.tsv", SearchOption.AllDirectories)
                     .Where(p => Path.GetFileName(p) != GeneratedLoc).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
            database.Merge(TsvTable.Read(path));
        return GenerateFromDatabase(database, profiles, battleRoot);
    }

    public static AttilaDatabase LoadPacks(string rpfm, string schema, IReadOnlyList<string> packsLowToHigh, string cacheRoot)
    {
        string schemaHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schema)));
        var extractedPacks = new List<string>();
        EnsurePlainDirectory(Path.GetFullPath(cacheRoot));
        foreach (string pack in packsLowToHigh)
        {
            var info = new FileInfo(pack);
            if (!info.Exists) throw new FileNotFoundException("An active Attila pack disappeared during battle generation.", pack);
            string signature = $"v2\n{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{schemaHash}";
            string bucket = Path.Combine(Path.GetFullPath(cacheRoot), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))));
            EnsurePlainDirectory(bucket);
            string marker = Path.Combine(bucket, "complete");
            if (new FileInfo(marker).LinkTarget != null)
                throw new InvalidOperationException("Stat fidelity cache marker is a link; choose a plain cache directory.");
            string? cache = null;
            if (File.Exists(marker))
            {
                if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Stat fidelity cache marker is a link; choose a plain cache directory.");
                string selected = File.ReadAllText(marker);
                if (!System.Text.RegularExpressions.Regex.IsMatch(selected, "^attempt-[a-f0-9]{32}$"))
                    throw new InvalidDataException("Invalid stat fidelity cache marker.");
                cache = Path.Combine(bucket, selected);
                EnsurePlainDirectory(cache, mustExist: true);
            }
            if (cache == null)
            {
                // Never recursively delete an incomplete cache. Each attempt owns a fresh plain directory.
                cache = Path.Combine(bucket, "attempt-" + Guid.NewGuid().ToString("N"));
                EnsurePlainDirectory(cache);
                string listing = RpfmRunner.Run(rpfm, "--game", "attila", "pack", "list", "-p", pack).Replace('\\', '/');
                var folders = AttilaDatabase.Keys.Keys.Where(t => t != "Loc").Select(t => "db/" + t)
                    .Append("text/db").Where(folder => listing.Contains(folder + "/", StringComparison.Ordinal)).ToList();
                if (folders.Count > 0)
                {
                    var arguments = new List<string> { "--game", "attila", "pack", "extract", "-p", pack, "-t", schema };
                    foreach (string folder in folders) { arguments.Add("-F"); arguments.Add(folder + ";" + cache); }
                    RpfmRunner.Run(rpfm, arguments.ToArray());
                    foreach (string file in Directory.GetFiles(cache, "*.tsv", SearchOption.AllDirectories)) TsvTable.Read(file);
                }
                File.WriteAllText(marker, Path.GetFileName(cache));
            }
            RejectLinksBelow(cache);
            extractedPacks.Add(cache);
        }
        return AttilaDatabase.FromPackDirectories(extractedPacks);
    }

    private static void EnsurePlainDirectory(string directory, bool mustExist = false)
    {
        if (new DirectoryInfo(directory).LinkTarget != null)
            throw new InvalidOperationException($"Stat fidelity cache directory is a link/reparse point: {directory}");
        if (Directory.Exists(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Stat fidelity cache directory is a link/reparse point: {directory}");
        }
        else if (mustExist) throw new DirectoryNotFoundException($"Completed stat fidelity cache is missing: {directory}");
        else Directory.CreateDirectory(directory);
    }

    private static void RejectLinksBelow(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Stat fidelity cache contains a link/reparse point: {entry}");
            if ((attributes & FileAttributes.Directory) != 0) RejectLinksBelow(entry);
        }
    }

    public static IReadOnlyDictionary<string, string> GenerateFromDatabase(AttilaDatabase database, IReadOnlyCollection<UnitStatProfile> profiles, string battleRoot)
    {
        var builder = new StatVariantBuilder(database);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in profiles) keys[profile.Id] = builder.Add(profile);
        // Validate every reference/profile before removing any previous output or writing a partial set.
        ClearGeneratedAt(battleRoot);
        foreach (var table in builder.Tables)
        {
            string path = table.Name == "Loc" ? Path.Combine(battleRoot, "text", "db", GeneratedLoc)
                : Path.Combine(battleRoot, "db", table.Name, GeneratedFile);
            table.Write(path);
        }
        return keys;
    }

    public static void ClearGeneratedTables(string runtimeRoot) => ClearGeneratedAt(Path.Combine(runtimeRoot, "data", "battle files"));

    private static void ClearGeneratedAt(string battleRoot)
    {
        foreach (string table in AttilaDatabase.Keys.Keys.Where(t => t != "Loc"))
        {
            string file = Path.Combine(battleRoot, "db", table, GeneratedFile);
            if (File.Exists(file)) File.Delete(file);
        }
        string loc = Path.Combine(battleRoot, "text", "db", GeneratedLoc);
        if (File.Exists(loc)) File.Delete(loc);
    }
}
