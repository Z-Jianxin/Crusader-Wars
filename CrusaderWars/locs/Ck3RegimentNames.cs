using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CrusaderWars.locs;

/// <summary>Literal CK3 English names, with active-mod precedence and a readable fallback.</summary>
public static class Ck3RegimentNames
{
    private static readonly Regex Entry = new(@"^\s*(?<key>[A-Za-z0-9_.-]+):(?:\d+)?\s*""(?<text>(?:\\.|[^""\\])*)""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Reference = new(@"\$(?<key>[A-Za-z0-9_.-]+)\$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyDictionary<string, string> Resolve(IEnumerable<string> localizationRootsLowToHigh, IEnumerable<string> requestedKeys)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string root in localizationRootsLowToHigh)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                foreach (string line in File.ReadLines(file))
                {
                    if (line.Length > 65536) continue;
                    var match = Entry.Match(line);
                    if (match.Success) values[match.Groups["key"].Value] = Unescape(match.Groups["text"].Value);
                }
        }
        return requestedKeys.Distinct(StringComparer.Ordinal).ToDictionary(key => key,
            key => ResolveValue(key, values, new HashSet<string>(StringComparer.Ordinal)) ?? Fallback(key), StringComparer.Ordinal);
    }

    private static string? ResolveValue(string key, IReadOnlyDictionary<string, string> values, HashSet<string> visiting)
    {
        if (visiting.Count >= 12 || !visiting.Add(key) || !values.TryGetValue(key, out string? value)) return null;
        bool unresolved = false;
        string expanded = Reference.Replace(value, match => {
            string? resolved = ResolveValue(match.Groups["key"].Value, values, new HashSet<string>(visiting, StringComparer.Ordinal));
            unresolved |= resolved == null;
            return resolved ?? "";
        });
        // Runtime scripted localization is not evaluated outside CK3.
        if (unresolved || expanded.IndexOfAny(new[] { '[', ']', '$', '#' }) >= 0) return null;
        expanded = Regex.Replace(expanded, @"\s+", " ").Trim();
        return expanded.Length > 0 ? expanded : null;
    }

    private static string Unescape(string value)
    {
        var result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                char escaped = value[++i];
                result.Append(escaped is 'n' or 'r' or 't' ? ' ' : escaped);
            }
            else result.Append(value[i]);
        }
        return result.ToString();
    }

    public static string Fallback(string key) => string.Join(" ", key.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
}
