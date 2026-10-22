using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.mod_manager;
using CrusaderWars.StatFidelity;
using CrusaderWars.unit_mapper;

internal static class MappingAudit
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Type Mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
    private static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private sealed record Row(string File, int Priority, int Order, string Domain, string Faction, string Role,
        string NativeType, string UnitKey, string Rank, string Level, string Percentage, string Maximum, bool Siege);
    private sealed record Resolution(string Faction, string NativeType, string? NativeCategory, bool Noncombatant,
        string UnitKey, string Route, string? File, string? Error = null, int? FallbackMaximum = null, NativeMappingDecision? Policy = null);
    private sealed record Finding(string Severity, string Code, string Faction, string NativeType, string UnitKey, string Detail);

    [STAThread]
    private static int Main(string[] args)
    {
        string original = Environment.CurrentDirectory;
        string? output = null;
        bool ownsOutput = false;
        try
        {
            var flags = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i += 2)
            {
                if (args[i] == "--help") { Console.WriteLine("Offline all-era mapping audit: --runtime DIR --ck3 EXE --ck3-user-dir DIR --attila EXE --native-catalog JSON --context-handoff LOG --context-living LIVING --context-player ID --context-combat ID --out NEW_DIR [--cache DIR] [--era FOLDER]"); return 0; }
                if (!args[i].StartsWith("--") || i + 1 == args.Length) throw new ArgumentException("Expected --name value.");
                flags.Add(args[i][2..], args[i + 1]);
            }
            string Required(string name) => Path.GetFullPath(flags.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing --" + name), original);
            string runtime = Required("runtime"), ck3 = Required("ck3"), user = Required("ck3-user-dir"), attila = Required("attila");
            output = Required("out");
            string[] protectedRoots = [runtime, user, Path.GetDirectoryName(Path.GetDirectoryName(ck3))!, Path.GetDirectoryName(attila)!];
            EnsureExternal(output, protectedRoots);
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Output must be a new directory.");
            Directory.CreateDirectory(output);
            ownsOutput = true;
            CopyTree(Path.Combine(runtime, "settings"), Path.Combine(output, "settings"));
            CopyTree(Path.Combine(runtime, "unit mappers"), Path.Combine(output, "unit mappers"));
            Directory.CreateDirectory(Path.Combine(output, "data"));
            Environment.CurrentDirectory = output;
            var options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", Static)!.GetValue(null)!;
            foreach (var option in XDocument.Load("settings/Options.xml").Descendants("Option")) options[option.Attribute("name")!.Value] = option.Value;
            options["SiegeEnginesInFieldBattles"] = "Enabled"; options["StatFidelityMode"] = "Enabled";
            SubmodManager.SetActiveSubmodsForPlaythrough("DefaultCK3", ["DahanChina"]);
            Mapper.GetProperty("ActivePlaythroughTag", Static)!.SetValue(null, "DefaultCK3");
            var definitions = NativeMaaDefinitions.LoadActive(ck3, user);
            // The matrix contains synthetic queries, but enables the production
            // fidelity policy with genuine, separately recorded native evidence.
            string contextLog = Required("context-handoff"), contextLiving = Required("context-living");
            string contextPlayer = flags.GetValueOrDefault("context-player") ?? throw new ArgumentException("Missing --context-player");
            string contextCombat = flags.GetValueOrDefault("context-combat") ?? throw new ArgumentException("Missing --context-combat");
            StatFidelitySession.Begin(File.ReadAllText(contextLog));
            StatFidelitySession.ValidateSaveEvidence(contextLiving, contextPlayer, contextCombat);
            typeof(ArmiesReader).GetField("nativeMaaDefinitions", Static)!.SetValue(null, definitions);
            Write("context.json", new { syntheticResolutionQueries = true, nativeHandoff = contextLog, nativeLiving = contextLiving,
                handoffSha256 = Hash(contextLog), livingSha256 = Hash(contextLiving), StatFidelitySession.SaveEvidenceValidated,
                purpose = "Exercise the actual bound-policy resolver. Does not claim the queried types/cultures are participating or recruitable in this native battle." });
            string catalogPath = Required("native-catalog");
            using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            string modList = Path.Combine(user, "dlc_load.json");
            if (!Hash(modList).Equals(catalog.RootElement.GetProperty("activeModList").GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native catalog active-mod list is stale; rebuild the catalog.");
            if (catalog.RootElement.TryGetProperty("inputFiles", out var inputs))
                foreach (var input in inputs.EnumerateObject())
                    if (!Hash(input.Name).Equals(input.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Native catalog source changed; rebuild the catalog: " + input.Name);
            var catalogTypes = catalog.RootElement.GetProperty("definitions").EnumerateArray()
                .ToDictionary(d => d.GetProperty("key").GetString()!, d => d.GetProperty("category").GetString());
            if (catalogTypes.Count != definitions.Count || definitions.Values.Any(d => !catalogTypes.TryGetValue(d.Key, out var category) || category != d.Category))
                throw new InvalidDataException("Native catalog type registry differs from the active production registry.");
            var cultureData = catalog.RootElement.GetProperty("cultures").EnumerateArray().ToArray();
            Write("native-definitions.json", definitions.Values);
            var packIndex = IndexPacks(attila);
            string cache = flags.TryGetValue("cache", out string? selectedCache) ? Path.GetFullPath(selectedCache, original) : Path.Combine(output, "cache");
            EnsureExternal(cache, protectedRoots);
            Write("inputs.json", new { createdUtc = DateTime.UtcNow, runtime, ck3, user, attila, cache,
                catalog = catalogPath, catalogSha256 = Hash(catalogPath), activeModListSha256 = Hash(modList),
                resolverAssemblySha256 = Hash(typeof(AvailableUnit).Assembly.Location),
                auditorAssemblySha256 = Hash(typeof(MappingAudit).Assembly.Location),
                activeSubmods = new[] { "DahanChina" }, manualExtrasIncluded = false,
                mapperFiles = Directory.GetFiles("unit mappers", "*.xml", SearchOption.AllDirectories).Order()
                    .Select(path => new { path, sha256 = Hash(path) }).ToArray() });
            var summaries = new List<object>();
            var databases = new Dictionary<string, PreparedAttilaDatabase>(StringComparer.Ordinal);
            foreach (string folder in Directory.GetDirectories("unit mappers").Where(p => Path.GetFileName(p).StartsWith("OfficialCC_DefaultCK3_", StringComparison.Ordinal)).Order())
            {
                string era = Path.GetFileName(folder);
                if (flags.TryGetValue("era", out var filter) && era != filter) continue;
                Console.WriteLine("Audit started: " + era);
                string resultFolder = Path.Combine(output, era); Directory.CreateDirectory(resultFolder);
                Mapper.GetProperty("LoadedUnitMapper_FolderPath", Static)!.SetValue(null, Path.GetFullPath(folder));
                Call("ClearFactionCache");
                var rows = new List<Row>();
                foreach (string domain in new[] { "Factions", "Titles" })
                {
                    string directory = Path.GetFullPath(Path.Combine(folder, domain));
                    if (!Directory.Exists(directory)) continue;
                    var files = (List<string>)Call("GetSortedFilePaths", directory, "OfficialCC_DefaultCK3_*")!;
                    for (int priority = 0; priority < files.Count; priority++)
                    {
                        var document = new XmlDocument(); Call("LoadMapperXml", document, files[priority]);
                        int order = 0;
                        foreach (XmlElement parent in document.DocumentElement!.ChildNodes.OfType<XmlElement>())
                            foreach (XmlElement row in parent.ChildNodes.OfType<XmlElement>())
                                if (row.HasAttribute("key")) rows.Add(new(Path.GetFileName(files[priority]), priority, order++, domain,
                                    domain == "Titles" ? parent.GetAttribute("title_key") : parent.GetAttribute("name"), row.Name, row.GetAttribute("type"), row.GetAttribute("key"), row.GetAttribute("rank"),
                                    row.GetAttribute("level"), row.GetAttribute("percentage"), row.GetAttribute("max"), row.GetAttribute("siege") == "true"));
                    }
                }
                Write(Path.Combine(resultFolder, "authored-rows.json"), rows);
                var (modNames, packPaths) = SelectPacks(Path.GetFullPath(folder), packIndex);
                var effectivePacks = PackSourceResolver.Resolve(attila, packPaths);
                Write(Path.Combine(resultFolder, "packs.json"), new { modNames, modsHighestFirst = packPaths, effectivePacksLowToHigh = effectivePacks.Select(p => new { path = p, length = new FileInfo(p).Length, modifiedUtc = File.GetLastWriteTimeUtc(p) }) });
                string stack = string.Join("\n", effectivePacks);
                if (!databases.TryGetValue(stack, out var prepared))
                    databases[stack] = prepared = StatFidelityBackend.PrepareFromPacks(runtime, effectivePacks, cache);
                typeof(StatFidelitySession).GetProperty("PreparedDatabase", Static)!.SetValue(null, prepared);
                var factions = rows.Where(r => r.Domain == "Factions").Select(r => r.Faction).Distinct().Order().ToArray();
                var cultures = new List<object>();
                var reachableFactions = new HashSet<string>(StringComparer.Ordinal) { "Default" };
                foreach (var culture in cultureData)
                {
                    string name = culture.GetProperty("name").GetString()!, heritage = culture.GetProperty("heritage").GetString()!;
                    string faction = (string)Call("GetAttilaFaction", name, heritage)!;
                    reachableFactions.Add(faction);
                    cultures.Add(new { name, heritage, faction });
                }
                foreach (string heritage in cultureData.Select(c => c.GetProperty("heritage").GetString()!).Distinct().Order())
                {
                    string faction = (string)Call("GetAttilaFaction", "__audit_dynamic_culture", heritage)!;
                    reachableFactions.Add(faction);
                    cultures.Add(new { name = "__audit_dynamic_culture", heritage, faction });
                }
                string unknownFaction = (string)Call("GetAttilaFaction", "__audit_unknown_culture", "__audit_unknown_heritage")!;
                string defaultFaction = (string)Call("GetAttilaFaction", "Default", "Default")!;
                reachableFactions.UnionWith([unknownFaction, defaultFaction]);
                cultures.Add(new { name = "__audit_unknown_culture", heritage = "__audit_unknown_heritage", faction = unknownFaction });
                cultures.Add(new { name = "Default", heritage = "Default", faction = defaultFaction });
                Write(Path.Combine(resultFolder, "culture-resolutions.json"), cultures);
                var resolutions = new List<Resolution>();
                var decisions = (IReadOnlyDictionary<int, NativeMappingDecision>)Mapper.GetProperty("NativeMappingDecisions", Static)!.GetValue(null)!;
                var authoredWinners = rows.Where(r => r.Domain == "Factions" && r.Role == "MenAtArm" && r.UnitKey.Length > 0)
                    .GroupBy(r => (r.Faction, r.NativeType)).ToDictionary(g => g.Key,
                        g => g.OrderByDescending(r => r.Priority).ThenBy(r => r.Order).First());
                foreach (string faction in reachableFactions.Order())
                    foreach (var native in definitions.Values.OrderBy(d => d.Key))
                    {
                        Row? Pick(string target) => authoredWinners.GetValueOrDefault((target, native.Key));
                        var winner = Pick(faction) ?? Pick("Default") ?? Pick("DEFAULT");
                        string route = winner == null ? "category-fallback" : winner.Faction == faction ? "specific" : "type-default";
                        var culture = new Culture("1", "audit", "heritage_audit");
                        var unit = new Unit(native.Key, 100, culture, RegimentType.MenAtArms, false, new Owner("0", culture)); unit.SetAttilaFaction(faction);
                        try
                        {
                            string key = (((string, bool))Call("GetUnitKey", unit)!).Item1;
                            if (winner != null && key != winner.UnitKey) route += "+runtime-policy";
                            int? fallbackMaximum = winner == null ? (int)Call("GetMax", unit)! : null;
                            resolutions.Add(new(faction, native.Key, native.Category, native.IsFieldNoncombatant, key, route, winner?.File,
                                FallbackMaximum: fallbackMaximum, Policy: decisions.GetValueOrDefault(unit.UniqueID)));
                        }
                        catch (TargetInvocationException error)
                        {
                            resolutions.Add(new(faction, native.Key, native.Category, native.IsFieldNoncombatant, "", route, winner?.File,
                                (error.InnerException ?? error).ToString(), Policy: decisions.GetValueOrDefault(unit.UniqueID)));
                        }
                    }
                Write(Path.Combine(resultFolder, "resolutions.json"), resolutions);
                var metadata = new Dictionary<string, AttilaUnitSemanticStats>();
                var missing = new Dictionary<string, string>();
                foreach (string key in rows.Select(r => r.UnitKey).Concat(resolutions.Select(r => r.UnitKey)).Where(k => k.Length > 0 && k != "not_found").Distinct().Order())
                    try { metadata[key] = prepared.QuerySemanticUnit(key); } catch (Exception error) { missing[key] = error.Message; }
                Write(Path.Combine(resultFolder, "unit-metadata.json"), metadata); Write(Path.Combine(resultFolder, "missing-assets.json"), missing);
                var findings = new List<Finding>();
                foreach (var row in resolutions) findings.AddRange(Classify(row, metadata, missing));
                foreach (var row in rows.Where(r => r.Role is "General" or "Knights" or "Levies" or "Garrison" || r.Domain == "Titles"))
                {
                    if (missing.TryGetValue(row.UnitKey, out var error)) findings.Add(new("error", "authored-missing-asset", row.Faction, row.Role + ":" + row.NativeType, row.UnitKey, error));
                    else if (metadata.TryGetValue(row.UnitKey, out var asset))
                    {
                        if (row.Role is "General" or "Knights" or "Levies" or "Garrison")
                            if (asset.IsNaval || asset.EngineKey.Length > 0) findings.Add(new("review", "authored-ordinary-role-special-asset", row.Faction, row.Role, row.UnitKey, asset.Category + "/" + asset.UnitClass));
                        if (row.Role == "Knights" && asset.HasMissileWeapon && asset.MissilePrecursor == false)
                            findings.Add(new("review", "knight-ranged-template-needs-representation-resolution", row.Faction, row.Role, row.UnitKey, "Audit authored candidate; runtime same-rank melee fallback can resolve it."));
                    }
                }
                Write(Path.Combine(resultFolder, "findings.json"), findings);
                prepared.AssertFresh();
                var summary = new { era, nativeTypes = definitions.Count, authoredFactions = factions.Length, reachableFactions = reachableFactions.Count, cultures = cultures.Count,
                    authoredRows = rows.Count, resolutions = resolutions.Count, assets = metadata.Count, missingAssets = missing.Count,
                    policyDecisions = resolutions.GroupBy(r => r.Policy?.Status ?? "none").Select(g => new { status = g.Key, count = g.Count() }),
                    resolutionErrors = findings.Count(f => f.Severity == "error" && !f.Code.StartsWith("authored-", StringComparison.Ordinal)),
                    authoredAssetErrors = findings.Count(f => f.Severity == "error" && f.Code.StartsWith("authored-", StringComparison.Ordinal)),
                    unexpectedResolverExceptions = resolutions.Count(r => r.Error != null && r.Policy?.Status != "unsupported"),
                    findings = findings.GroupBy(f => new { f.Severity, f.Code }).Select(g => new { g.Key.Severity, g.Key.Code, count = g.Count() }),
                    scope = "All static culture/heritage/default routes to reachable faction/default resolution matrix, plus all authored title/role assets. Recruitment conditions are not evaluated; not every culture can recruit every type. Authored factions unreachable by these culture contexts are inventoried, not exhaustively resolved.", gameLaunched = false, installedFilesWritten = false };
                Write(Path.Combine(resultFolder, "summary.json"), summary); summaries.Add(summary);
                Console.WriteLine("Audit completed: " + era + "; " + resolutions.Count + " resolutions; " + findings.Count + " findings.");
            }
            if (summaries.Count == 0) throw new ArgumentException("No era matched the selected filter.");
            Write(Path.Combine(output, "summary.json"), new { completed = true, summaries, gameLaunched = false, installedFilesWritten = false });
            return 0;
        }
        catch (Exception error)
        {
            if (ownsOutput && output != null) Write(Path.Combine(output, "failure.json"), new { error = error.ToString(), gameLaunched = false, installedFilesWritten = false });
            Console.Error.WriteLine(error); return 1;
        }
        finally { Environment.CurrentDirectory = original; }
    }

    private static IEnumerable<Finding> Classify(Resolution row, IReadOnlyDictionary<string, AttilaUnitSemanticStats> metadata, IReadOnlyDictionary<string, string> missing)
    {
        Finding Finding(string severity, string code, string detail) => new(severity, code, row.Faction, row.NativeType, row.UnitKey, detail);
        if (row.Error != null)
        {
            bool expected = row.Policy?.Status == "unsupported";
            yield return Finding(expected ? "unsupported" : "error", expected ? "resolver-rejected-profile" : "resolver-exception", row.Error);
            yield break;
        }
        if (row.Noncombatant) yield break; // Mapping stock is not proof of a field actor.
        if (row.Policy?.Status == "unsupported") { yield return Finding("unsupported", "resolver-rejected-profile", row.Policy.Reason); yield break; }
        if (row.FallbackMaximum is <= 0) yield return Finding("error", "fallback-has-no-positive-formation-capacity", row.FallbackMaximum.ToString()!);
        if (row.UnitKey is "" or "not_found") { yield return Finding("error", "unmapped-native-type", row.Route); yield break; }
        if (missing.TryGetValue(row.UnitKey, out string? error)) { yield return Finding("error", "missing-asset", error); yield break; }
        if (!metadata.TryGetValue(row.UnitKey, out var unit)) yield break;
        if (unit.MissingReferences.Count > 0) yield return Finding("error", "broken-asset-reference", string.Join(";", unit.MissingReferences));
        if (!unit.HasVisualVariant) yield return Finding("error", "missing-visual-variant", unit.LandUnitKey);
        if (unit.IsNaval || unit.EngineKey.Length > 0) { yield return Finding("error", "combatant-mapped-to-ship-or-artillery", unit.Category + "/" + unit.UnitClass); yield break; }
        string category = row.NativeCategory ?? "unknown";
        bool mounted = category.Contains("cavalry") || category is "camel" or "elephant";
        bool foot = category is "archers" or "heavy_infantry" or "pikemen" or "skirmishers" or "gunpowder" or "peasant_militia";
        if (mounted && !unit.IsMounted && !unit.HasSpecialEntity) yield return Finding("error", "mounted-native-mapped-to-foot", unit.Category + "/" + unit.UnitClass);
        if (foot && (unit.IsMounted || unit.HasSpecialEntity)) yield return Finding("error", "foot-native-mapped-to-mounted-or-special", unit.Category + "/" + unit.UnitClass);
        if (category is "archers" or "archer_cavalry" or "gunpowder")
            if (!unit.HasMissileWeapon || unit.MissilePrecursor != false || unit.Ammunition <= 0)
                yield return Finding("error", "ranged-native-without-sustained-missile", "Missile=" + unit.MissileWeaponKey + "; precursor=" + unit.MissilePrecursor + "; ammo=" + unit.Ammunition);
        if (category is "heavy_infantry" or "pikemen" or "heavy_cavalry" or "light_cavalry")
            if (unit.HasMissileWeapon && unit.MissilePrecursor == false && (unit.Category == "inf_ranged" || unit.UnitClass is "inf_mis" or "cav_mis" or "cav_hyb"))
                yield return Finding("review", "melee-native-mapped-to-sustained-ranged-role", unit.Category + "/" + unit.UnitClass + "; native families can contain intentional hybrids");
        if (category == "pikemen" && unit.UnitClass is not ("inf_pik" or "inf_spr" or "inf_pik_hyb" or "inf_spr_hyb") &&
            unit.MeleeWeaponType is not ("spear" or "long_spear" or "pike" or "long_pike"))
            yield return Finding("review", "pike-native-without-verified-polearm-role", unit.UnitClass + "/" + unit.MeleeWeaponType + "; inspect actual equipment/animation before changing");
        if (category == "heavy_cavalry" && unit.Weight is "light" or "very_light")
            yield return Finding("review", "heavy-cavalry-light-weight", "Weight=" + unit.Weight + "; armour=" + unit.ArmourKey + "; mount=" + unit.MountKey);
        if (!unit.CanScaleHitPoints) yield return Finding("unsupported", "unverified-hp-representation", unit.Category + "/" + unit.UnitClass + "; mount=" + unit.MountKey + "; animal=" + unit.AnimalKey);
        if (category == "camel_cavalry" && unit.MountEntityType == "horse")
            yield return Finding("error", "camel-native-mapped-to-horse", unit.MountKey);
        if (category == "elephant_cavalry" && unit.MountEntityType is "horse" or "camel")
            yield return Finding("error", "elephant-native-mapped-to-horse-or-camel", unit.MountKey);
        if (category is "camel_cavalry" or "elephant_cavalry" or "gunpowder" or "nomadic_horde")
            yield return Finding("review", "special-native-family-review", category + "; actual=" + unit.UnitClass + "/" + unit.MountEntityType + "/" + unit.ProjectileCategory);
    }

    private static (string[] Names, string[] Paths) SelectPacks(string mapperFolder, IReadOnlyDictionary<string, string[]> index)
    {
        var document = XDocument.Load(Path.Combine(mapperFolder, "Mods.xml"));
        var selected = document.Root!.Elements("Submod").Where(e => e.Attribute("submod_tag")?.Value == "DahanChina").ToArray();
        var replaced = selected.SelectMany(e => (e.Attribute("replace")?.Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = document.Root.Elements("Mod").Select(e => e.Value.Trim()).Where(name => !replaced.Contains(name))
            .Concat(selected.SelectMany(e => e.Elements("Mod")).Select(e => e.Value.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (names, names.Select(name => index.TryGetValue(name, out var found) && found.Length > 0 ? found[0] : throw new FileNotFoundException("Missing selected pack " + name)).ToArray());
    }
    private static IReadOnlyDictionary<string, string[]> IndexPacks(string executable)
    {
        string root = Path.GetDirectoryName(executable)!, data = Path.Combine(root, "data");
        var directory = new DirectoryInfo(root); while (directory != null && !directory.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
        var workshop = AttilaModManager.DiscoverWorkshopPackFiles(directory == null ? null : Path.Combine(directory.FullName, "workshop", "content", "325610"));
        return Directory.GetFiles(data, "*.pack").Concat(workshop).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.OrderBy(p => Path.GetDirectoryName(p) == data ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    private static object? Call(string name, params object?[] args) => Mapper.GetMethod(name, Static)!.Invoke(null, args);
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void EnsureExternal(string path, IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            string relative = Path.GetRelativePath(root, path);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)))
                throw new InvalidOperationException("Audit output/cache must stay outside the runtime, game and CK3 user directories.");
        }
    }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        { string destination = Path.Combine(target, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination); }
    }
}
