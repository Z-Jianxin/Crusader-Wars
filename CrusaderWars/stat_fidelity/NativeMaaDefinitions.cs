using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CrusaderWars.StatFidelity;

/// <summary>Native MAA semantics from the active CK3 sources, never from an Attila mapper flag.</summary>
public static class NativeMaaDefinitions
{
    private const string Folder = "common/men_at_arms_types";

    public static IReadOnlyDictionary<string, NativeMaaDefinition> LoadActive(string ck3Executable, string ck3UserDirectory)
    {
        string installation = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(ck3Executable)))
            ?? throw new InvalidDataException("CK3 installation path is unavailable for native MAA semantics.");
        string game = Path.Combine(installation, "game");
        if (!Directory.Exists(Path.Combine(game, Folder)))
            throw new DirectoryNotFoundException("Native CK3 MAA definitions are missing: " + Path.Combine(game, Folder));
        string config = Path.Combine(Path.GetFullPath(ck3UserDirectory), "dlc_load.json");
        if (new FileInfo(config).Length > 4 * 1024 * 1024) throw new InvalidDataException("Active CK3 mod list exceeds bounds.");
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        if (!json.RootElement.TryGetProperty("enabled_mods", out var enabled) || enabled.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Active CK3 mod list has no enabled_mods array.");
        var roots = new List<(string Root, string[] ReplacePaths)> { (game, Array.Empty<string>()) };
        var descriptors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in enabled.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || roots.Count > 2048) throw new InvalidDataException("Invalid or excessive enabled CK3 mods.");
            string descriptor = Path.GetFullPath(item.GetString()!, ck3UserDirectory);
            if (!descriptors.Add(descriptor)) throw new InvalidDataException("Duplicate active CK3 mod descriptor.");
            var data = NativeCombatParticipation.ReadModDescriptor(File.ReadAllText(descriptor));
            if (string.IsNullOrWhiteSpace(data.Path)) throw new InvalidDataException("Active CK3 mod lacks an inspectable path: " + descriptor);
            string root = Path.GetFullPath(data.Path, ck3UserDirectory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Active CK3 source mod is missing: " + root);
            roots.Add((root, data.ReplacePaths));
        }
        return LoadRoots(roots);
    }

    public static IReadOnlyDictionary<string, NativeMaaDefinition> LoadRoots(IEnumerable<(string Root, string[] ReplacePaths)> rootsLowToHigh)
    {
        var files = new Dictionary<string, (string File, int Priority)>(StringComparer.OrdinalIgnoreCase);
        int priority = 0;
        foreach (var source in rootsLowToHigh)
        {
            foreach (string raw in source.ReplacePaths)
            {
                string replacement = raw.Replace('\\', '/').Trim('/');
                if (Folder.Equals(replacement, StringComparison.OrdinalIgnoreCase) || Folder.StartsWith(replacement + "/", StringComparison.OrdinalIgnoreCase))
                    files.Clear();
                else if (replacement.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = replacement[(Folder.Length + 1)..];
                    foreach (string key in files.Keys.Where(k => k.Equals(prefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)).ToArray()) files.Remove(key);
                }
            }
            string folder = Path.Combine(source.Root, Folder);
            if (Directory.Exists(folder))
                foreach (string file in Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories))
                {
                    if (files.Count > 10000) throw new InvalidDataException("Native MAA file count exceeds bounds.");
                    files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = (file, priority);
                }
            priority++;
        }
        var result = new Dictionary<string, NativeMaaDefinition>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(f => f.Value.Priority).ThenBy(f => f.Key, StringComparer.Ordinal))
            foreach (var definition in NativeCombatParticipation.ReadMaaDefinitions(File.ReadAllText(file.Value.File), file.Value.File))
                result[definition.Key] = definition;
        return result;
    }
}
