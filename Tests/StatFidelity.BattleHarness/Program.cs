using CrusaderWars;
using CrusaderWars.armies;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
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
                Console.WriteLine("Offline CK3 battle replay: --runtime DIR --save FILE --log FILE --out NEW_DIR [--mapper DefaultCK3] [--export-only] [--attila EXE] [--stat-settings JSON]");
                Console.WriteLine("Copies inputs into a new output directory. Never launches games or writes a campaign save. --attila additionally generates database TSV variants from installed packs, without loading a game.");
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
            stage = "read-log";
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
                    foreach (var unit in army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms && !u.IsSiege()))
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
                CopyTree(Path.Combine(runtime, "data", "rpfm"), Path.Combine(output, "data", "rpfm"));
                File.Copy(Path.Combine(runtime, "data", "schema_att.ron"), Path.Combine(output, "data", "schema_att.ron"));
                StatFidelityBackend.Generate(output, Path.GetFullPath(attila, originalDirectory), StatFidelitySession.PendingProfiles);
                databaseGenerated = true;
            }
            var battleXml = Directory.GetFiles(Path.Combine(output, "data", "battle files"), "tut_battle.xml", SearchOption.AllDirectories).Single();
            XDocument.Load(battleXml); // Verify generated XML is structurally readable.
            WriteJson(Path.Combine(output, "result.json"), new
            {
                success = true, stage = "complete", BattleResult.CombatID,
                playerId = DataSearch.Player_Character?.GetID(), snapshotId = StatFidelitySession.Export.BattleId,
                StatFidelitySession.SaveEvidenceValidated, mapper = UnitMappers_BETA.GetLoadedUnitMapperName(), requiredPacks,
                attackerArmies = attackers.Count, defenderArmies = defenders.Count,
                generatedProfiles = StatFidelitySession.PendingProfiles.Count, databaseGenerated,
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
        units = army.Units.Select(u => new { u.UniqueID, name = u.GetName(), type = u.GetRegimentType().ToString(), soldiers = u.GetSoldiers(), u.SourceIdentity, u.SourceOwnerId, u.OriginalAttilaKey, key = u.GetAttilaUnitKey() }),
        merged = (army.MergedArmies ?? new List<Army>()).Select(ArmySummary)
    };

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
            if (args[i] is "--help" or "--export-only") { result[args[i][2..]] = ""; continue; }
            if (!args[i].StartsWith("--") || i + 1 == args.Length) throw new ArgumentException("Expected --name value. Use --help.");
            string key = args[i][2..];
            if (key is not ("runtime" or "save" or "log" or "out" or "mapper" or "attila" or "stat-settings")) throw new ArgumentException("Unknown option: " + key);
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
