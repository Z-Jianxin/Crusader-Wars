using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CrusaderWars.StatFidelity;

public static class PackSourceResolver
{
    /// <summary>CA boot/release/patch packs, enabled mods (script lists highest first), then automatic movie packs.</summary>
    public static IReadOnlyList<string> Resolve(string attilaExecutable)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(attilaExecutable))!;
        string data = Path.Combine(root, "data");
        if (!File.Exists(Path.Combine(data, "data.pack")))
            throw new FileNotFoundException("Stat fidelity requires Attila's data.pack. Finish installing or verifying Attila before creating a battle.");
        string script = Path.Combine(root, "used_mods_cc.txt");
        if (!File.Exists(script)) throw new FileNotFoundException("The active Attila load-order file used_mods_cc.txt is missing.", script);

        string locale = ResolveLocale(data);
        var automatic = Directory.GetFiles(data, "*.pack")
            .Where(p => !Path.GetFileName(p).Equals("CrusaderConflicts.pack", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(p).StartsWith("cc-building-", StringComparison.OrdinalIgnoreCase)
                     && IsSelectedLocalePack(Path.GetFileName(p), locale))
            .Select(p => (path: p, type: PackType(p))).ToList();
        var result = automatic.Where(x => x.type is 0 or 1 or 2).OrderBy(x => x.type)
            .ThenBy(x => Path.GetFileName(x.path).Equals("local_en_shared_rome2.pack", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(x => Path.GetFileName(x.path), StringComparer.OrdinalIgnoreCase).Select(x => x.path).ToList();
        result.AddRange(ResolveScript(script, data).Reverse());
        result.AddRange(automatic.Where(x => x.type == 4).OrderByDescending(x => Path.GetFileName(x.path), StringComparer.OrdinalIgnoreCase).Select(x => x.path));
        // Preserve the final (highest-priority) occurrence if a movie pack was also explicitly enabled.
        return result.AsEnumerable().Reverse().Distinct(StringComparer.OrdinalIgnoreCase).Reverse().ToArray();
    }

    public static string ResolveLocale(string dataDirectory)
    {
        string languageFile = Path.Combine(dataDirectory, "language.txt");
        if (File.Exists(languageFile))
        {
            string code = File.ReadAllText(languageFile).Trim().Trim('\uFEFF').ToUpperInvariant();
            return code switch
            {
                "EN" => "en", "CZ" => "cz", "FR" => "fr", "DE" or "GE" => "ge",
                "IT" => "it", "PO" or "PL" => "pl", "RU" => "ru", "ES" or "SP" => "sp", "TR" => "tr",
                _ => throw new InvalidDataException($"Unsupported Attila language code '{code}' in data/language.txt; stat fidelity will not guess localized unit names.")
            };
        }
        var locales = Directory.GetFiles(dataDirectory, "local_*.pack").Select(Path.GetFileName)
            .Where(name => name != null && Regex.IsMatch(name, "^local_[a-z]{2}\\.pack$", RegexOptions.IgnoreCase))
            .Select(name => name!.Substring(6, 2).ToLowerInvariant()).Distinct().ToArray();
        if (locales.Length == 1) return locales[0];
        if (locales.Length == 0) return "en"; // An overhaul may supply its own names; no competing language rows exist.
        throw new InvalidDataException("Attila data/language.txt is missing and several language packs are installed; restore the language setting before generating stat variants.");
    }

    private static bool IsSelectedLocalePack(string name, string locale) =>
        !name.StartsWith("local_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("local_en_shared_rome2.pack", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("local_" + locale + ".", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("local_" + locale + "_", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ResolveScript(string scriptPath, string dataDirectory)
    {
        string text = File.ReadAllText(scriptPath);
        var directories = new List<string> { Path.GetFullPath(dataDirectory) };
        foreach (Match match in Regex.Matches(text, "(?m)^\\s*add_working_directory\\s+\"([^\"]+)\"\\s*;"))
        {
            string path = match.Groups[1].Value;
            directories.Add(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(scriptPath)!, path)));
        }
        var result = new List<string>();
        foreach (Match match in Regex.Matches(text, "(?m)^\\s*mod\\s+\"([^\"]+)\"\\s*;"))
        {
            string name = match.Groups[1].Value;
            if (name.Equals("CrusaderConflicts.pack", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(name) != name) throw new InvalidDataException($"Expected a pack filename in used_mods_cc.txt, got '{name}'.");
            var matches = directories.Select(d => Path.Combine(d, name)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count == 0) throw new FileNotFoundException($"Active Attila pack '{name}' is missing. Restore the required mod before creating the battle.");
            if (matches.Count > 1) throw new InvalidOperationException($"Active pack '{name}' exists in more than one working directory; remove the duplicate so stat fidelity can match the game's load order.");
            result.Add(matches[0]);
        }
        return result;
    }

    private static int PackType(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != "PFH4") throw new InvalidDataException($"Unsupported or incomplete Attila pack '{path}' ({magic}).");
        return (int)(reader.ReadUInt32() & 15);
    }
}
