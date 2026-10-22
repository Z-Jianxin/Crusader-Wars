using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CrusaderWars.StatFidelity;

internal sealed class CavalryAssetEvidence
{
    internal Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, HashSet<string>> Animations { get; } = new(StringComparer.Ordinal);

    internal static CavalryAssetEvidence Read(IReadOnlyList<string> directoriesLowToHigh)
    {
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directoriesLowToHigh)
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (relative.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("animations/animation_tables/", StringComparison.OrdinalIgnoreCase)) winners[relative] = path;
            }
        var result = new CavalryAssetEvidence();
        foreach (var (relative, path) in winners)
        {
            byte[] bytes = File.ReadAllBytes(path);
            result.Files[relative] = Convert.ToHexString(SHA256.HashData(bytes));
            if (!relative.StartsWith("animations/animation_tables/", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(bytes), @"animation_table\s+(\S+)\s*\{([^}]+)\}"))
            {
                string key = match.Groups[1].Value;
                if (!result.Animations.TryGetValue(key, out var hashes)) result.Animations[key] = hashes = new(StringComparer.Ordinal);
                hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match.Groups[2].Value.Replace("\r\n", "\n").Replace("\r", "\n")))));
            }
        }
        return result;
    }

    internal bool Matches(string variant, string variantHash, string rider, string riderHash, string mount, string mountHash) =>
        Files.GetValueOrDefault("variantmeshes/variantmeshdefinitions/" + variant + ".variantmeshdefinition") == variantHash &&
        AnimationMatches(rider, riderHash) && AnimationMatches(mount, mountHash);
    private bool AnimationMatches(string key, string hash) => Animations.TryGetValue(key, out var hashes) && hashes.Count == 1 && hashes.Contains(hash);
}

public static partial class StatFidelityBackend
{
    private static string LoadCavalryAssets(string rpfm, string pack, string cacheRoot, string databaseBucketName)
    {
        string root = Path.Combine(cacheRoot, "cavalry-assets-v1", databaseBucketName);
        EnsurePlainDirectory(root);
        string? completed = CompletedAttempt(root);
        if (completed != null) { RejectLinksBelow(completed); return completed; }
        string attempt = Path.Combine(root, "attempt-" + Guid.NewGuid().ToString("N"));
        EnsurePlainDirectory(attempt);
        string listing = RpfmRunner.Run(rpfm, "--game", "attila", "pack", "list", "-p", pack).Replace('\\', '/');
        var args = new List<string> { "--game", "attila", "pack", "extract", "-p", pack };
        foreach (string folder in new[] { "animations/animation_tables", "variantmeshes/variantmeshdefinitions" })
            if (listing.Contains(folder + "/", StringComparison.Ordinal)) { args.Add("-F"); args.Add(folder + ";" + attempt); }
        if (args.Count > 6) RpfmRunner.Run(rpfm, args.ToArray());
        RejectLinksBelow(attempt);
        File.WriteAllText(Path.Combine(root, "complete"), Path.GetFileName(attempt));
        return attempt;
    }
}
