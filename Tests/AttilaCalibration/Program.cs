using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CrusaderWars.StatFidelity;
using MoonSharp.Interpreter;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: AttilaCalibration <CC-runtime-root> <Attila-game-root> <new-output-directory> [infantry|mounted|native] [replicates=4]");
    return 2;
}
string runtime = Path.GetFullPath(args[0]);
string game = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
string suite = args.Length > 3 ? args[3] : "infantry";
int repeats = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 4;
if (suite != "infantry" && suite != "mounted" && suite != "native") throw new ArgumentException("Suite must be infantry, mounted or native.");
if (repeats < 1 || repeats > 4) throw new ArgumentException("Use 1–4 replicates per launch; repeat launches for more samples.");
if (output.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    output.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || output == game || output == runtime)
    throw new ArgumentException("Output must be a task-owned scratch directory outside the game and runtime installations.");
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Output directory is not empty. Use a new directory; existing calibration results are preserved.");
Directory.CreateDirectory(output);
string rpfm = Path.Combine(runtime, "data", "rpfm", "rpfm_cli.exe");
string schema = Path.Combine(runtime, "data", "schema_att.ron");
string data = Path.Combine(game, "data");
if (!File.Exists(Path.Combine(game, "Attila.exe")) || !File.Exists(Path.Combine(data, "data.pack")))
    throw new FileNotFoundException("Wait for a completed Attila install (executable and data.pack required).");
if (suite == "native")
{
    NativeControl.Prepare(rpfm, schema, game, output);
    return 0;
}
string locale = PackSourceResolver.ResolveLocale(data);
var nativePacks = new[] { "boot.pack", "data.pack", "local_en_shared_rome2.pack", $"local_{locale}.pack" }
    .Select(name => Path.Combine(data, name)).Where(File.Exists).ToArray();
var database = StatFidelityBackend.LoadPacks(rpfm, schema, nativePacks, Path.Combine(output, "cache"));

var candidates = database.Tables["main_units_tables"].Rows.Where(row => row.GetValueOrDefault("is_naval") != "true")
    .OrderBy(row => row["unit"].StartsWith("att_rom_", StringComparison.Ordinal) ? 0 : 1)
    .ThenBy(row => row["unit"], StringComparer.Ordinal).ToList();
Dictionary<string, string>? selectedMain = null;
Dictionary<string, string>? selectedLand = null;
foreach (var row in candidates)
{
    try
    {
        var land = database.Require("land_units_tables", row["land_unit"]);
        bool mounted = !string.IsNullOrEmpty(land.GetValueOrDefault("mount"));
        if (mounted != (suite == "mounted") || !string.IsNullOrEmpty(land.GetValueOrDefault("engine")) ||
            !string.IsNullOrEmpty(land.GetValueOrDefault("animal")) || !string.IsNullOrEmpty(land.GetValueOrDefault("chariot")) ||
            land.GetValueOrDefault("category") is not ("inf_melee" or "cavalry") ||
            !string.IsNullOrEmpty(land.GetValueOrDefault("primary_missile_weapon"))) continue;
        new StatVariantBuilder(database).Add(new("probe", row["unit"], 1, 1));
        selectedMain = row; selectedLand = land; break;
    }
    catch (InvalidOperationException) { }
}
if (selectedMain == null || selectedLand == null) throw new InvalidOperationException("No self-contained vanilla melee unit found for this suite.");
string baseUnit = selectedMain["unit"];
Console.WriteLine($"Base unit: {baseUnit}; land unit: {selectedLand["key"]}");

var builder = new StatVariantBuilder(database);
var cases = suite == "infantry" ? new[] { "baseline", "native", "damage2", "hp2" } : new[] { "baseline", "man_hp2", "mount_hp2", "both_hp2" };
var caseKeys = new Dictionary<string, string>();
foreach (string label in cases)
    caseKeys[label] = label == "native" ? baseUnit : builder.Add(new("calibration-" + suite + "-" + label, baseUnit,
        label == "damage2" ? 2 : 1, label == "hp2" ? 2 : 1));
var generated = builder.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);

if (suite == "mounted")
{
    // Explicit experimental fixture only. Production mounted HP != 1 remains rejected.
    string extra = Path.Combine(output, "mount-source"); Directory.CreateDirectory(extra);
    RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", Path.Combine(data, "data.pack"), "-t", schema,
        "-F", "db/mounts_tables;" + extra);
    var sourceTables = Directory.GetFiles(extra, "*.tsv", SearchOption.AllDirectories).Select(TsvTable.Read).ToList();
    var sourceMount = sourceTables.SelectMany(t => t.Rows.Select(r => (table: t, row: r)))
        .Single(x => x.row["key"] == selectedLand["mount"]);
    var mounts = new TsvTable("mounts_tables", sourceMount.table.Version, sourceMount.table.Columns);
    generated.Add(mounts.Name, mounts);
    foreach (string label in cases.Where(c => c != "baseline"))
    {
        string key = caseKeys[label];
        var land = generated["land_units_tables"].Rows.Single(r => r["key"] == key);
        var man = generated["battle_entities_tables"].Rows.Single(r => r["key"] == land["man_entity"]);
        if (label is "man_hp2" or "both_hp2") man["hit_points"] = Twice(man["hit_points"]);
        if (label is "mount_hp2" or "both_hp2")
        {
            string mountKey = key + "_mount"; string entityKey = key + "_mount_entity";
            var mount = new Dictionary<string, string>(sourceMount.row) { ["key"] = mountKey, ["entity"] = entityKey };
            mounts.Rows.Add(mount);
            var entity = new Dictionary<string, string>(database.Require("battle_entities_tables", sourceMount.row["entity"])) { ["key"] = entityKey };
            entity["hit_points"] = Twice(entity["hit_points"]);
            generated["battle_entities_tables"].Rows.Add(entity);
            land["mount"] = mountKey;
        }
    }
}

string payload = Path.Combine(output, "payload"); Directory.CreateDirectory(payload);
foreach (var table in generated.Values)
    table.Write(table.Name == "Loc" ? Path.Combine(payload, "text", "db", "cc_calibration.loc.tsv")
        : Path.Combine(payload, "db", table.Name, "cc_calibration.tsv"));
// Reuse a native historical entry, terrain and environment. The root operator launches it
// through the normal menu, after the frontend has finished its introductory sequence.

var matches = new List<MatchSpec>();
foreach (string label in cases)
    for (int replicate = 0; replicate < repeats; replicate++)
    {
        bool mirrored = replicate % 2 == 1;
        matches.Add(new(label + "_r" + (replicate + 1), mirrored ? caseKeys["baseline"] : caseKeys[label],
            mirrored ? caseKeys[label] : caseKeys["baseline"], mirrored, label));
    }
var battle = new XElement("battle");
for (int side = 0; side < 2; side++)
{
    int sign = side == 0 ? -1 : 1;
    var army = new XElement("army", new XElement("army_name", "Calibration " + (side + 1)),
        new XElement("faction", "att_fact_western_roman_empire"), new XElement("can_withdraw", "false"),
        new XElement("deployment_area", new XElement("centre", new XAttribute("x", sign * 300), new XAttribute("y", 0)),
            new XElement("width", new XAttribute("metres", 650)), new XElement("height", new XAttribute("metres", 1400)),
            new XElement("orientation", new XAttribute("radians", side == 0 ? "1.57" : "4.71"))));
    var sentinel = MakeUnit("sentinel" + (side + 1), baseUnit, sign * 650, 0, side, 12);
    sentinel.Add(new XElement("general", new XElement("name", "Calibration observer"), new XElement("star_rating", new XAttribute("level", 0))));
    army.Add(sentinel);
    for (int i = 0; i < matches.Count; i++)
    {
        double y = (i - (matches.Count - 1) / 2.0) * 80;
        var match = matches[i];
        army.Add(MakeUnit((side == 0 ? "left_" : "right_") + (i + 1), side == 0 ? match.LeftKey : match.RightKey, sign * 35, y, side, 60));
    }
    battle.Add(new XElement("alliance", new XAttribute("id", side), army,
        new XElement("victory_condition", new XElement("kill_or_rout_enemy")),
        new XElement("rout_position", new XAttribute("x", sign * 5000), new XAttribute("y", 0))));
}
battle.Add(new XElement("battle_description", new XElement("battle_script", new XAttribute("prepare_for_fade_in", "false"), "cc_calibration.lua"),
    new XElement("time_of_day", "day"), new XElement("season", "Summer"), new XElement("precipitation_type", "dry"),
    new XElement("type", "land_normal"), new XElement("timeout_winning_alliance_index", 1), new XElement("battlefield_owner_alliance_index", 1)),
    new XElement("playable_area", new XAttribute("dimension", 1924)),
    new XElement("weather", new XElement("environment_key", @"Script\AN_Adrianople\AN.environment"),
        new XElement("prevailing_wind", new XAttribute("x", "1.00"), new XAttribute("y", "0.00"))),
    new XElement("sea_surface_name", "wind_level_4"),
    new XElement("battle_map_definition", new XElement("name", "terrain/battles/adrianople/")));
string scripts = Path.Combine(payload, "script", "an_adrianople"); Directory.CreateDirectory(scripts);
new XDocument(new XDeclaration("1.0", "utf-8", null), battle).Save(Path.Combine(scripts, "an_battle.xml"));
string battleLua = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "calibration.lua"))
    .Replace("@@LOGFILE@@", LuaPath(Path.Combine(output, "battle.csv"))).Replace("@@SUITE@@", suite)
    .Replace("@@PAIRS@@", string.Join(",\n", matches.Select((m, i) => $"    {{ label = \"{m.Label}\", left = \"left_{i + 1}\", right = \"right_{i + 1}\" }}")));
new Script().LoadString(battleLua); // Compile only; this does not execute game APIs.
File.WriteAllText(Path.Combine(scripts, "cc_calibration.lua"), battleLua, new UTF8Encoding(false));

// Use vanilla's frontend: the installed CC version requires dev.lua/util.lua, which this
// intentionally minimal fixture does not include. Importing that version would abort early.
string nativeFrontend = Path.Combine(output, "native-frontend"); Directory.CreateDirectory(nativeFrontend);
RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", Path.Combine(data, "data.pack"),
    "-f", "lua_scripts/frontend_hbs.lua;" + nativeFrontend);
string frontEnd = "function cc_calibration_frontend_log(message)\n" +
    "    local f = io.open(\"" + LuaPath(Path.Combine(output, "frontend.log")) + "\", \"a\")\n" +
    "    if f then f:write(tostring(os.time()) .. \" \" .. tostring(message) .. \"\\n\"); f:close() end\nend\n" +
    "cc_calibration_frontend_log(\"frontend module loaded; launch Adrianople from historical battle menu\")\n" +
    File.ReadAllText(Path.Combine(nativeFrontend, "lua_scripts", "frontend_hbs.lua")).Replace(
        "AddEventCallBack(\"HistoricBattleEvent\", OnHistoricBattleEvent);",
        "AddEventCallBack(\"HistoricBattleEvent\", function(context)\n" +
        "    cc_calibration_frontend_log(\"HistoricBattleEvent: \" .. tostring(context.string))\n" +
        "    OnHistoricBattleEvent(context)\nend);") +
    "\nAddEventCallBack(\"UICreated\", function(context) cc_calibration_frontend_log(\"UICreated\") end);\n" +
    "\ncc_calibration_frontend_log(\"native frontend module finished\")\n";
new Script().LoadString(frontEnd);
string frontendPath = Path.Combine(payload, "lua_scripts", "frontend_hbs.lua"); Directory.CreateDirectory(Path.GetDirectoryName(frontendPath)!);
File.WriteAllText(frontendPath, frontEnd, new UTF8Encoding(false));

string packOutput = Path.Combine(output, "cc_stat_calibration.pack");
RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", packOutput);
RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", packOutput, "-t", schema, "-F", payload);
string listing = RpfmRunner.Run(rpfm, "--game", "attila", "pack", "list", "-p", packOutput);
if (!listing.Contains("script/an_adrianople/cc_calibration.lua") || !listing.Contains("cc_calibration")) throw new InvalidDataException("Calibration pack is missing generated content.");
string roundtripRoot = Path.Combine(output, "roundtrip"); Directory.CreateDirectory(roundtripRoot);
RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", packOutput, "-t", schema, "-F", "db;" + roundtripRoot, "-F", "text;" + roundtripRoot);
foreach (var table in generated.Values)
{
    string path = table.Name == "Loc" ? Path.Combine(roundtripRoot, "text", "db", "cc_calibration.loc.tsv")
        : Path.Combine(roundtripRoot, "db", table.Name, "cc_calibration.tsv");
    var actual = TsvTable.Read(path);
    if (actual.Rows.Count != table.Rows.Count || actual.Version != table.Version)
        throw new InvalidDataException("Calibration roundtrip mismatch: " + table.Name);
    for (int i = 0; i < table.Rows.Count; i++)
        foreach (string column in table.Columns)
            if (actual.Rows[i][column] != table.Rows[i][column])
                throw new InvalidDataException($"Calibration roundtrip changed {table.Name} row {i} column {column}.");
}
File.WriteAllText(Path.Combine(output, "used_mods_cc_calibration.txt"), $"add_working_directory \"{output.Replace('\\', '/')}\";\nmod \"cc_stat_calibration.pack\";\n");
File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
{
    suite, repeats, baseUnit, sourcePacks = nativePacks, chosenLand = selectedLand, cases = caseKeys,
    matches, log = Path.Combine(output, "battle.csv"), timeoutSeconds = 300, nativeBuild = "25370302",
    launch = "manual_historical_battle_menu", battle = "Adrianople", terrain = "terrain/battles/adrianople/",
    generatedStatRecords = generated.Where(x => x.Key is "land_units_tables" or "battle_entities_tables" or "melee_weapons_tables" or "mounts_tables")
        .ToDictionary(x => x.Key, x => x.Value.Rows),
    confidence = "Fixture generated, Lua compiled and all generated TSV values survived binary pack roundtrip. Engine execution and combat calibration pending."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Prepared {matches.Count} paired trials: {packOutput}");
Console.WriteLine("No game was launched. Use only the generated mod-list file, with all unrelated mods disabled.");
Console.WriteLine("Launch Historical Battles > Adrianople through the normal menu; there is no startup timer.");
return 0;

static string Twice(string value) => checked(int.Parse(value, CultureInfo.InvariantCulture) * 2).ToString(CultureInfo.InvariantCulture);
static string LuaPath(string path) => path.Replace('\\', '/').Replace("\"", "\\\"");
static XElement MakeUnit(string name, string key, double x, double y, int side, int soldiers) => new("unit",
    new XAttribute("num_soldiers", soldiers), new XAttribute("script_name", name),
    new XElement("unit_type", new XAttribute("type", key)),
    new XElement("position", new XAttribute("x", x.ToString(CultureInfo.InvariantCulture)), new XAttribute("y", y.ToString(CultureInfo.InvariantCulture))),
    new XElement("orientation", new XAttribute("radians", side == 0 ? "1.57" : "4.71")),
    new XElement("width", new XAttribute("metres", 35)), new XElement("unit_experience", new XAttribute("level", 0)));

record MatchSpec(string Label, string LeftKey, string RightKey, bool Mirrored, string Case);
