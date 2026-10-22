using CrusaderWars;
using CrusaderWars.armies;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.locs;
using CrusaderWars.StatFidelity;
using CrusaderWars.twbattle;
using CrusaderWars.unit_mapper;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ICSharpCode.SharpZipLib.Zip;

internal static class Replay
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        string originalDirectory = Environment.CurrentDirectory;
        string? output = null;
        string stage = "arguments";
        try
        {
            var flags = ParseArguments(args);
            if (flags.ContainsKey("help"))
            {
                Console.WriteLine("Offline CK3 battle replay: --runtime DIR --save FILE --log FILE --out NEW_DIR [--mapper DefaultCK3] [--export-only | --snapshot-only --player-id ID] [--attila EXE] [--stat-cache DIR] [--stat-settings JSON] [--casualty-smoke]");
                Console.WriteLine("Copies inputs into a new output directory. Never launches games or writes a campaign save. --attila additionally generates database TSV variants from installed packs, without loading a game.");
                Console.WriteLine("--ck3-user-dir DIR selects the source CK3 user directory for read-only active-mod/landed-title lookups; defaults to Documents/Paradox Interactive/Crusader Kings III.");
                return 0;
            }
            string runtime = Path.GetFullPath(Required(flags, "runtime"));
            string save = Path.GetFullPath(Required(flags, "save"));
            string log = Path.GetFullPath(Required(flags, "log"));
            output = Path.GetFullPath(Required(flags, "out"));
            if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("--out must be a new directory; no existing output is overwritten.");
            string relativeOutput = Path.GetRelativePath(runtime, output);
            if (relativeOutput == "." || (!relativeOutput.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relativeOutput)))
                throw new ArgumentException("--out must be outside the installed runtime, so copying assets cannot recurse into its own output.");
            if (!Directory.Exists(runtime) || !File.Exists(save) || !File.Exists(log)) throw new FileNotFoundException("The runtime, save and log inputs must exist.");
            string mapperTag = flags.GetValueOrDefault("mapper", "DefaultCK3");
            Directory.CreateDirectory(output);
            stage = "isolate-inputs";
            CopyTree(Path.Combine(runtime, "data", "battle files"), Path.Combine(output, "data", "battle files"));
            CopyTree(Path.Combine(runtime, "data", "traits"), Path.Combine(output, "data", "traits"));
            CopyTree(Path.Combine(runtime, "data", "terrains_images"), Path.Combine(output, "data", "terrains_images"));
            CopyTree(Path.Combine(runtime, "data", "units_cards_names"), Path.Combine(output, "data", "units_cards_names"));
            CopyTree(Path.Combine(runtime, "unit mappers"), Path.Combine(output, "unit mappers"));
            CopyTree(Path.Combine(runtime, "settings"), Path.Combine(output, "settings"));
            Directory.CreateDirectory(Path.Combine(output, "data", "save_file_data", "temp"));
            Directory.CreateDirectory(Path.Combine(output, "data", "save_file_data", "gamestate_file"));
            Directory.CreateDirectory(Path.Combine(output, "inputs"));
            File.Copy(log, Path.Combine(output, "inputs", "console.log"));
            File.Copy(save, Path.Combine(output, "inputs", "handoff.ck3"));
            var manifest = new
            {
                schemaVersion = 1, createdUtc = DateTime.UtcNow, runtime, save, log, mapperTag,
                saveSha256 = Hash(Path.Combine(output, "inputs", "handoff.ck3")), logSha256 = Hash(Path.Combine(output, "inputs", "console.log")),
                gameLaunched = false, campaignSaveWritten = false,
                implementationVersion = typeof(StatFidelitySession).Assembly.GetName().Version?.ToString()
            };
            WriteJson(Path.Combine(output, "inputs.json"), manifest);
            Environment.CurrentDirectory = output;
            string settingsPath = Path.Combine(output, "settings", "StatFidelity.json");
            // A replay defaults to strict evidence. Existing user settings cannot silently
            // enable partial coverage; optional overrides must be supplied explicitly.
            File.WriteAllText(settingsPath, flags.TryGetValue("stat-settings", out var customSettings)
                ? File.ReadAllText(Path.GetFullPath(customSettings, originalDirectory))
                : JsonSerializer.Serialize(new StatFidelitySettings(), Json));
            LoadOptions();
            Data.Reset();
            Unit.ResetUniqueIDCounter();
            string isolatedSave = Path.Combine(output, "inputs", "handoff.ck3");
            string gamestate = Path.Combine(output, "data", "save_file_data", "gamestate_file", "gamestate");
            stage = "extract-save";
            ExtractSave(isolatedSave, gamestate);
            string logText = HomePage.RemoveASCII(File.ReadAllText(Path.Combine(output, "inputs", "console.log")));
            if (flags.ContainsKey("snapshot-only"))
            {
                stage = "native-snapshot-validation";
                ValidateSnapshotOnly(gamestate, logText, Required(flags, "player-id"), output);
                Console.WriteLine("Native snapshot validated: " + output);
                return 0;
            }
            stage = "read-log";
            ConfigureReadOnlyGamePaths(flags.GetValueOrDefault("ck3-user-dir",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paradox Interactive", "Crusader Kings III")), output);
            DataSearch.Search(logText);
            if (BattleState.IsSiegeBattle) throw new NotSupportedException("This replay harness currently validates field battles only.");
            stage = "load-mapper";
            var requiredPacks = UnitMappers_BETA.GetUnitMapperModFromTagAndTimePeriod(mapperTag, new List<string>());
            if (UnitMappers_BETA.GetLoadedUnitMapperName() == null) throw new InvalidOperationException("No mapper matches the log date and --mapper tag.");
            stage = "read-save";
            Reader.ReadFile(gamestate);
            BattleResult.GetPlayerCombatResult();
            BattleResult.ReadPlayerCombat(CK3LogData.LeftSide.GetCommander().id);
            stage = "read-armies-and-validate-evidence";
            var (attackers, defenders) = ArmiesReader.ReadBattleArmies();
            if (flags.ContainsKey("export-only"))
            {
                stage = "validate-regiment-coverage";
                foreach (var army in attackers.Concat(defenders))
                    foreach (var unit in army.Units.Where(u => u.GetRegimentType() is RegimentType.MenAtArms or RegimentType.Levy && !u.IsSiege()))
                    {
                        string key = unit.GetAttilaUnitKey();
                        if (string.IsNullOrWhiteSpace(key) || key == UnitMappers_BETA.NOT_FOUND_KEY)
                            throw new InvalidOperationException("Unmapped participating regiment: " + unit.SourceIdentity);
                        StatFidelitySession.Apply(unit, army, key);
                    }
                StatFidelitySession.WriteCoverageReport(output);
                WriteJson(Path.Combine(output, "profiles.json"), StatFidelitySession.PendingProfiles);
                WriteJson(Path.Combine(output, "armies.json"), attackers.Concat(defenders).Select(ArmySummary));
                WriteJson(Path.Combine(output, "result.json"), new
                {
                    success = true, stage = "export-validated", BattleResult.CombatID,
                    playerId = DataSearch.Player_Character?.GetID(), snapshotId = StatFidelitySession.Export.BattleId,
                    StatFidelitySession.SaveEvidenceValidated, mapper = UnitMappers_BETA.GetLoadedUnitMapperName(),
                    attackerArmies = attackers.Count, defenderArmies = defenders.Count,
                    plannedRegimentProfiles = StatFidelitySession.PendingProfiles.Count,
                    battleGenerated = false, databaseGenerated = false, gameLaunched = false, campaignSaveWritten = false
                });
                Console.WriteLine("Export validated: " + output);
                return 0;
            }
            BattleFile.SetArmiesSides(attackers, defenders);
            UnitsFile.ResetProcessedArmies();
            StatFidelitySession.ResetGeneration();
            if (StatFidelitySession.Settings.EnableKnightCalibration && StatFidelitySession.Settings.KnightBudgetMode == "Anchored")
            {
                stage = "prepare-knight-database";
                EnsureBackendAssets(runtime, output);
                string attilaForBudget = flags.TryGetValue("attila", out var selectedAttila) ? Path.GetFullPath(selectedAttila, originalDirectory) : CrusaderWars.Properties.Settings.Default.VAR_attila_path;
                string? cache = flags.TryGetValue("stat-cache", out var selectedCache) ? Path.GetFullPath(selectedCache, originalDirectory) : null;
                StatFidelitySession.PrepareKnightBudgetDatabase(output, attilaForBudget, cache);
            }
            foreach (var army in attackers.Concat(defenders)) StatFidelitySession.PrepareKnightCalibration(army);
            if (!ModOptions.CombineKnightsEnabled())
                typeof(BattleProcessor).GetMethod("ProcessProminentKnights", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { attackers, defenders });
            stage = "generate-battle";
            TerrainGenerator.CheckForSpecialCrossingBattle(attackers, defenders);
            BattleFile.ClearFile();
            DeclarationsFile.Erase();
            BattleScript.EraseScript(false);
            Data.units_scripts.Clear();
            UnitsCardsNames.RemoveFiles();
            var leftSide = ArmiesReader.GetSideArmies("left", attackers, defenders);
            var rightSide = ArmiesReader.GetSideArmies("right", attackers, defenders);
            if (leftSide == null || rightSide == null || leftSide.Count == 0 || rightSide.Count == 0)
                throw new InvalidOperationException("Current battle has no complete left/right side context.");
            BattleDetails.ChangeBattleDetails(leftSide.Sum(a => a.GetTotalSoldiers()), rightSide.Sum(a => a.GetTotalSoldiers()),
                leftSide[0].CombatSide, rightSide[0].CombatSide);
            BattleScript.CreateScript();
            int soldiers = attackers.Concat(defenders).Sum(a => a.Units.Sum(u => u.GetSoldiers()));
            ArmyProportions.AutoSizeUnits(soldiers);
            foreach (var army in attackers.Concat(defenders)) army.ScaleUnits(ArmyProportions.BattleScale);
            BattleLog.Reset();
            BattleFile.BETA_CreateBattle(attackers, defenders);
            BattleScript.CloseScript();
            BattleScript.SetCommandersLocals();
            BattleScript.SetLocalsKills(Data.units_scripts);
            BattleScript.CloseScript();
            if (BattleLog.HasUnmappedUnits()) throw new InvalidOperationException("Battle generation dropped unmapped units; inspect data/debug.log. Replay is not considered valid.");
            StatFidelitySession.WriteCoverageReport(output);
            WriteJson(Path.Combine(output, "profiles.json"), StatFidelitySession.PendingProfiles);
            WriteJson(Path.Combine(output, "armies.json"), attackers.Concat(defenders).Select(ArmySummary));
            bool databaseGenerated = false;
            if (flags.TryGetValue("attila", out var attila))
            {
                stage = "generate-database";
                EnsureBackendAssets(runtime, output);
                if (StatFidelitySession.PreparedDatabase != null)
                    StatFidelitySession.PreparedDatabase.Generate(StatFidelitySession.PendingProfiles, Path.Combine(output, "data", "battle files"));
                else StatFidelityBackend.Generate(output, Path.GetFullPath(attila, originalDirectory), StatFidelitySession.PendingProfiles);
                databaseGenerated = true;
            }
            var battleXml = Directory.GetFiles(Path.Combine(output, "data", "battle files"), "tut_battle.xml", SearchOption.AllDirectories).Single();
            var battleDocument = XDocument.Load(battleXml);
            var emittedScripts = battleDocument.Descendants("unit").Select(u => u.Attribute("script_name")?.Value ?? "").ToArray();
            var generatedCharacters = attackers.Concat(defenders).SelectMany(a => new[] { a }.Concat(a.MergedArmies)).DistinctBy(a => a.ID)
                .SelectMany(a => a.Units).Where(u => u.GetSoldiers() > 0 && u.GetRegimentType() is RegimentType.Commander or RegimentType.Knight).ToArray();
            foreach (var unit in generatedCharacters)
                if (emittedScripts.Count(s => s.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal)) != 1)
                    throw new InvalidOperationException("A character placeholder remained in the battle roster without exactly one XML formation: " + unit.UniqueID);
            object? casualtySmoke = null;
            if (flags.ContainsKey("casualty-smoke"))
            {
                stage = "synthetic-casualty-roundtrip";
                casualtySmoke = CasualtySmoke.Run(attackers, defenders, gamestate, battleXml, output);
            }
            WriteJson(Path.Combine(output, "result.json"), new
            {
                success = true, stage = "complete", BattleResult.CombatID,
                playerId = DataSearch.Player_Character?.GetID(), snapshotId = StatFidelitySession.Export.BattleId,
                parsedBattleDate = new { Date.Year, Date.Month, Date.Day },
                handoffContext = HandoffLog.SelectLatest(logText, true).Fields,
                battlePresentation = new { BattleDetails.Name, leftRealm = CK3LogData.LeftSide.GetRealmName(), rightRealm = CK3LogData.RightSide.GetRealmName() },
                StatFidelitySession.SaveEvidenceValidated, mapper = UnitMappers_BETA.GetLoadedUnitMapperName(), requiredPacks,
                attackerArmies = attackers.Count, defenderArmies = defenders.Count,
                generatedProfiles = StatFidelitySession.PendingProfiles.Count, generatedCharacterFormations = generatedCharacters.Length, databaseGenerated, casualtySmoke,
                gameLaunched = false, campaignSaveWritten = false,
                validationLimit = "Offline conversion only; no claim of Attila engine loading, battle balance, or gameplay casualty return."
            });
            Console.WriteLine("Replay complete: " + output);
            return 0;
        }
        catch (Exception exception)
        {
            var error = exception is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : exception;
            if (output != null && Directory.Exists(output) && Environment.CurrentDirectory == output)
            {
                StatFidelitySession.WriteCoverageReport(output);
                WriteJson(Path.Combine(output, "result.json"), new { success = false, stage, error = error.ToString(), gameLaunched = false, campaignSaveWritten = false });
            }
            Console.Error.WriteLine("Replay failed at " + stage + ": " + error.Message);
            return 1;
        }
        finally { Environment.CurrentDirectory = originalDirectory; }
    }

    private static object ArmySummary(Army army) => new
    {
        army.ID, army.CombatSide, army.CommanderID,
        units = army.Units.Select(u => new { u.UniqueID, name = u.GetName(), type = u.GetRegimentType().ToString(), soldiers = u.GetSoldiers(), u.SourceIdentity, u.SourceOwnerId, u.SourceOriginProvinceId, u.OriginalAttilaKey, key = u.GetAttilaUnitKey() }),
        merged = (army.MergedArmies ?? new List<Army>()).Select(ArmySummary)
    };

    private static void ValidateSnapshotOnly(string gamestate, string log, string playerId, string output)
    {
        // This diagnostic mode intentionally does not invent legacy battle log fields.
        // It checks a real native snapshot and its save independently of GUI handoff.
        StatFidelitySession.Begin(log);
        Reader.ReadFile(gamestate);
        BattleResult.ReadPlayerCombat(StatFidelitySession.Export.Commanders["attacker"].CharacterId);
        if (string.IsNullOrWhiteSpace(BattleResult.Player_Combat)) throw new InvalidOperationException("No saved combat matched the exported attacker commander.");
        StatFidelitySession.ValidateSaveEvidence(Writter.DataFilesPaths.Living_Path(), playerId, BattleResult.CombatID ?? "");
        var attackerField = typeof(ArmiesReader).GetField("attacker_armies", BindingFlags.NonPublic | BindingFlags.Static)!;
        var defenderField = typeof(ArmiesReader).GetField("defender_armies", BindingFlags.NonPublic | BindingFlags.Static)!;
        var attackers = (List<Army>)attackerField.GetValue(null)!;
        var defenders = (List<Army>)defenderField.GetValue(null)!;
        attackers.Clear(); defenders.Clear();
        InvokeArmyReader("ReadCombatArmies", BattleResult.Player_Combat);
        attackers = (List<Army>)attackerField.GetValue(null)!;
        defenders = (List<Army>)defenderField.GetValue(null)!;
        InvokeArmyReader("ReadArmiesData");
        InvokeArmyReader("ReadArmiesUnits");
        InvokeArmyReader("ReadArmyRegiments");
        InvokeArmyReader("ReadRegiments");
        if (attackers.Count == 0 || defenders.Count == 0) throw new InvalidOperationException("The selected save combat has no two-sided army roster.");
        var coverage = new List<object>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var knightCoverage = new List<object>();
        foreach (var army in attackers.Concat(defenders))
        {
            foreach (var armyRegiment in army.ArmyRegiments.Where(r => r.Type == RegimentType.MenAtArms))
                foreach (var source in armyRegiment.Regiments)
                {
                    string owner = string.IsNullOrEmpty(source.Owner) ? army.Owner?.GetID() ?? "" : source.Owner;
                    var rows = StatFidelitySession.Export.Regiments.Where(r => r.Side == army.CombatSide &&
                        r.RegimentId == source.ID && r.OwnerId == owner && (r.ArmyId == "none" || r.ArmyId == army.ID) &&
                        (r.Type == "source" || r.Type == armyRegiment.MAA_Name)).ToArray();
                    if (rows.Length != 1) throw new InvalidOperationException("Snapshot has " + rows.Length + " matches for source " + army.CombatSide + ":" + army.ID + ":" + source.ID + ":" + owner);
                    var stats = rows[0];
                    string origin = string.IsNullOrEmpty(source.Origin) ? "none" : source.Origin;
                    if (origin != stats.StationProvinceId) throw new InvalidOperationException("Native origin/station mismatch for " + source.ID);
                    matched.Add(stats.Identity);
                    coverage.Add(new { side = army.CombatSide, armyId = army.ID, armyRegimentId = armyRegiment.ID,
                        sourceId = source.ID, chunkIndex = source.Index, ownerId = owner, originProvinceId = origin, type = armyRegiment.MAA_Name,
                        source.CurrentNum, source.Max, stats.Damage, stats.Toughness, stats.BaseDamage, stats.BaseToughness,
                        ratioAvailable = stats.BaseDamage > 0 && stats.BaseToughness > 0 });
                }
            foreach (var knight in army.ArmyRegiments.Where(r => r.Type == RegimentType.Knight))
            {
                if (!StatFidelitySession.Export.Knights.TryGetValue(army.CombatSide + ":" + knight.MAA_Name, out var stats))
                    throw new InvalidOperationException("Snapshot is missing native knight " + army.CombatSide + ":" + knight.MAA_Name);
                knightCoverage.Add(new { side = army.CombatSide, armyId = army.ID, stats.CharacterId, stats.Prowess, stats.Effectiveness,
                    stats.DamagePerProwess, stats.ToughnessPerProwess, isCommander = army.CommanderID == stats.CharacterId });
            }
        }
        string[] unmatchedExportRows = StatFidelitySession.Export.Regiments.Where(r => !matched.Contains(r.Identity)).Select(r => r.Identity).ToArray();
        if (unmatchedExportRows.Length > 0) throw new InvalidOperationException("Export includes regiment identities absent from the saved combat: " + string.Join(", ", unmatchedExportRows));
        WriteJson(Path.Combine(output, "native-coverage.json"), new { regiments = coverage, knights = knightCoverage });
        StatFidelitySession.WriteCoverageReport(output);
        WriteJson(Path.Combine(output, "result.json"), new
        {
            success = true, stage = "native-snapshot-validated", BattleResult.CombatID, playerId,
            snapshotId = StatFidelitySession.Export.BattleId, StatFidelitySession.SaveEvidenceValidated,
            attackerArmies = attackers.Count, defenderArmies = defenders.Count,
            coveredSourceChunks = coverage.Count, coveredKnights = knightCoverage.Count,
            battleGenerated = false, databaseGenerated = false, gameLaunched = false, campaignSaveWritten = false,
            validationLimit = "Native export/save binding and source coverage only. No legacy log fields were fabricated; mapper/battle generation is untested in this mode."
        });
    }

    private static void InvokeArmyReader(string method, params object[] arguments) =>
        typeof(ArmiesReader).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);

    private static void LoadOptions()
    {
        // Read the same option names without invoking WinForms validation dialogs.
        var document = XDocument.Load(Path.Combine("settings", "Options.xml"));
        ModOptions.optionsValuesCollection.Clear();
        foreach (var option in document.Descendants("Option"))
            if (option.Attribute("name")?.Value is string name) ModOptions.optionsValuesCollection[name] = option.Value;
        ModOptions.optionsValuesCollection["StatFidelityMode"] = "Enabled";
        ModOptions.optionsValuesCollection["CloseCK3"] = "Disabled";
        ModOptions.optionsValuesCollection["CloseAttila"] = "Disabled";
        ModOptions.optionsValuesCollection["AutoScaleUnits"] = "Disabled";
        ModOptions.optionsValuesCollection["BattleScale"] = "100%";
    }

    private static void ConfigureReadOnlyGamePaths(string userDirectory, string output)
    {
        userDirectory = Path.GetFullPath(userDirectory);
        if (!File.Exists(Path.Combine(userDirectory, "dlc_load.json")))
            throw new FileNotFoundException("Pass --ck3-user-dir for the CK3 user directory used by this fixture.");
        var paths = XDocument.Load(Path.Combine("settings", "Paths.xml"));
        CrusaderWars.Properties.Settings.Default.VAR_ck3_path = paths.Descendants("CrusaderKings").Single().Attribute("path")?.Value ?? "";
        CrusaderWars.Properties.Settings.Default.VAR_attila_path = paths.Descendants("TotalWarAttila").Single().Attribute("path")?.Value ?? "";
        CrusaderWars.Properties.Settings.Default.VAR_log_ck3 = Path.Combine(userDirectory, "console_history.txt");
        // Only the original game and mod definitions are read. No persistent user
        // settings are saved, and campaign-output settings point into isolation.
        CrusaderWars.Properties.Settings.Default.VAR_dir_save = Path.Combine(output, "isolated-campaign", "save games");
        CrusaderWars.Properties.Settings.Default.VAR_log_attila = Path.Combine(output, "inputs", "unused-attila.log");
        File.Copy(Path.Combine(userDirectory, "dlc_load.json"), Path.Combine(output, "inputs", "dlc_load.json"));
    }

    private static void EnsureBackendAssets(string runtime, string output)
    {
        if (!Directory.Exists(Path.Combine(output, "data", "rpfm")))
            CopyTree(Path.Combine(runtime, "data", "rpfm"), Path.Combine(output, "data", "rpfm"));
        if (!File.Exists(Path.Combine(output, "data", "schema_att.ron")))
            File.Copy(Path.Combine(runtime, "data", "schema_att.ron"), Path.Combine(output, "data", "schema_att.ron"));
    }

    private static void ExtractSave(string save, string gamestate)
    {
        // Only extract the gamestate member. Archive paths never become write paths.
        using var stream = File.OpenRead(save);
        long zipStart = -1;
        uint signature = 0;
        for (long offset = 0; offset < Math.Min(stream.Length, 16 * 1024 * 1024); offset++)
        {
            signature = (signature << 8) | (byte)stream.ReadByte();
            if (signature == 0x504b0304) { zipStart = offset - 3; break; }
        }
        if (zipStart >= 0)
        {
            stream.Position = zipStart;
            using var archive = new ZipFile(stream);
            var entry = archive.GetEntry("gamestate") ?? throw new InvalidDataException("Save archive has no gamestate member.");
            using var input = archive.GetInputStream(entry);
            using var destination = File.Create(gamestate);
            input.CopyTo(destination);
        }
        else File.Copy(save, gamestate);
        // The production reader only uses text metadata fields, all before the ZIP.
        using var metadataInput = new StreamReader(save);
        using var metadata = new StreamWriter(Path.Combine("data", "save_file_data", "metadata.txt"), false, new UTF8Encoding(false));
        for (int lines = 0; lines < 10000 && metadataInput.ReadLine() is string line; lines++)
        {
            metadata.WriteLine(line);
            if (line == "}") break;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "--export-only" or "--snapshot-only" or "--casualty-smoke") { result[args[i][2..]] = ""; continue; }
            if (!args[i].StartsWith("--") || i + 1 == args.Length) throw new ArgumentException("Expected --name value. Use --help.");
            string key = args[i][2..];
            if (key is not ("runtime" or "save" or "log" or "out" or "mapper" or "attila" or "stat-settings" or "player-id" or "ck3-user-dir" or "stat-cache")) throw new ArgumentException("Unknown option: " + key);
            if (!result.TryAdd(key, args[++i])) throw new ArgumentException("Repeated option: " + key);
        }
        return result;
    }
    private static string Required(Dictionary<string, string> values, string name) => values.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing --" + name);
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Runtime asset links are not followed: " + path);
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyTree(path, target);
            else File.Copy(path, target);
        }
    }
}
