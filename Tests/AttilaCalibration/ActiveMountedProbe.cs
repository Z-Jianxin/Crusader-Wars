using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrusaderWars.StatFidelity;
using MoonSharp.Interpreter;

internal static class ActiveMountedProbe
{
    public static void Prepare(string runtime, string rpfm, string schema, string game, string output,
        string cacheRoot, string profilesFile, bool automaticLaunch)
    {
        var profiles = JsonSerializer.Deserialize<UnitStatProfile[]>(File.ReadAllText(profilesFile))!;
        if (profiles.Length != 3 || profiles[0].BaseUnitKey != "att_rom_catafractarii" ||
            profiles[1].BaseUnitKey != profiles[0].BaseUnitKey || profiles[2].BaseUnitKey != "cha_gen_knights")
            throw new InvalidDataException("Probe requires neutral Catafractarii, proportional HP2 Catafractarii, and the real mapped knight profile.");
        if (profiles[0].DamageMultiplier != 1 || profiles[0].HitPointMultiplier != 1 ||
            profiles[1].DamageMultiplier != 1 || profiles[1].HitPointMultiplier != 2)
            throw new InvalidDataException("The Catafractarii pair must isolate proportional HP scaling.");
        var database = StatFidelityBackend.Prepare(runtime, Path.Combine(game, "Attila.exe"), cacheRoot);
        string payload = Path.Combine(output, "payload");
        var keys = database.Generate(profiles, payload);
        var statFiles = Directory.GetFiles(payload, "*.tsv", SearchOption.AllDirectories);
        string native = Path.Combine(output, "native-source"); Directory.CreateDirectory(native);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", Path.Combine(game, "data", "data.pack"),
            "-f", "lua_scripts/frontend_hbs.lua;" + native, "-F", "script/an_adrianople;" + native,
            "-F", "db/battles_tables;" + native, "-t", schema);
        foreach (string source in Directory.GetFiles(Path.Combine(native, "script"), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(payload, Path.GetRelativePath(native, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
        string xmlPath = Path.Combine(payload, "script", "an_adrianople", "an_battle.xml");
        string xml; Encoding encoding;
        using (var reader = new StreamReader(xmlPath, Encoding.UTF8, true)) { xml = reader.ReadToEnd(); encoding = reader.CurrentEncoding; }
        string[] subjects = ["Vgt_02", "Vgt_03", "Vgt_04"];
        int[] counts = [120, 120, 43];
        for (int i = 0; i < subjects.Length; i++)
        {
            string pattern = "<unit script_name=\"" + subjects[i] + "\">.*?</unit>";
            var matches = Regex.Matches(xml, pattern, RegexOptions.Singleline);
            if (matches.Count != 1 || XElement.Parse(matches[0].Value).Element("general") != null)
                throw new InvalidDataException("Expected one ordinary native unit: " + subjects[i]);
            string changed = matches[0].Value.Replace("<unit script_name=\"" + subjects[i] + "\">",
                "<unit script_name=\"" + subjects[i] + "\" num_soldiers=\"" + counts[i] + "\">");
            changed = Regex.Replace(changed, "(<unit_type\\s+type=\")[^\"]+(\")", m => m.Groups[1].Value + keys[profiles[i].Id] + m.Groups[2].Value);
            changed = Regex.Replace(changed, "<unit_experience\\s+level=\"[^\"]*\"/>", "<unit_experience level=\"0\"/>");
            if ((string?)XElement.Parse(changed).Element("unit_experience")?.Attribute("level") != "0")
                throw new InvalidDataException("The active probe needs explicit zero XP: " + subjects[i]);
            xml = xml.Replace(matches[0].Value, changed);
        }
        File.WriteAllText(xmlPath, xml, encoding);
        var battleEntry = Directory.GetFiles(Path.Combine(native, "db", "battles_tables"), "*.tsv").Select(TsvTable.Read)
            .SelectMany(t => t.Rows.Where(r => r.GetValueOrDefault("key") == "Adrianople").Select(r => (table: t, row: r))).Single();
        var battleTable = new TsvTable("battles_tables", battleEntry.table.Version, battleEntry.table.Columns);
        battleTable.Rows.Add(battleEntry.row);
        battleTable.Write(Path.Combine(payload, "db", "battles_tables", "cc_active_mounted_probe.tsv"));
        string logPath = Path.Combine(output, "native-smoke.log").Replace('\\', '/');
        string logger = "function cc_active_probe_log(message)\n local f = io.open(" + JsonSerializer.Serialize(logPath) + ", \"a\")\n if f then f:write(tostring(os.time()) .. \" \" .. tostring(message) .. \"\\n\"); f:close() end\nend\n";
        string frontend = logger + "cc_active_probe_log(\"frontend loaded\")\n" + File.ReadAllText(Path.Combine(native, "lua_scripts", "frontend_hbs.lua"));
        if (automaticLaunch) frontend += HistoricalAutostart.Script("cc_active_probe_log");
        new Script().LoadString(frontend);
        Directory.CreateDirectory(Path.Combine(payload, "lua_scripts"));
        File.WriteAllText(Path.Combine(payload, "lua_scripts", "frontend_hbs.lua"), frontend, new UTF8Encoding(false));
        string battlePath = Path.Combine(payload, "script", "an_adrianople", "an_start.lua");
        string script = logger + "cc_active_probe_log(\"native battle script loaded\")\n" + File.ReadAllText(battlePath) + "\ncc_active_probe_log(\"native setup finished\")\n";
        foreach (string subject in subjects)
            script += "cc_active_probe_log(\"" + subject + " type=\" .. SUnit_" + subject + ".unit:type() .. \" men=\" .. SUnit_" + subject + ".unit:number_of_men_alive())\n";
        script += "for _, phase in ipairs({\"Deployment\", \"Deployed\", \"Complete\"}) do local label = phase; bm:register_phase_change_callback(phase, function() cc_active_probe_log(\"PHASE \" .. label) end) end\n";
        new Script().LoadString(script);
        File.WriteAllText(battlePath, script, new UTF8Encoding(false));
        string pack = Path.Combine(output, "cc_stat_calibration.pack");
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", pack);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", pack, "-t", schema, "-F", payload);
        string roundtrip = Path.Combine(output, "roundtrip"); Directory.CreateDirectory(roundtrip);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", pack, "-t", schema, "-F", "db;" + roundtrip, "-F", "text;" + roundtrip);
        foreach (string source in statFiles)
        {
            var expected = TsvTable.Read(source); var actual = TsvTable.Read(Path.Combine(roundtrip, Path.GetRelativePath(payload, source)));
            if (actual.Version != expected.Version || actual.Rows.Count != expected.Rows.Count)
                throw new InvalidDataException("Roundtrip changed table shape: " + source);
            for (int i = 0; i < expected.Rows.Count; i++) foreach (string col in expected.Columns)
                if (actual.Rows[i][col] != expected.Rows[i][col]) throw new InvalidDataException("Roundtrip changed " + source + ":" + col);
        }
        string startup = "show_frontend_movies false;\nadd_working_directory \"" + output.Replace('\\', '/') + "\";\nmod \"cc_stat_calibration.pack\";\n";
        startup += string.Join("\n", File.ReadAllLines(Path.Combine(game, "used_mods_cc.txt"))
            .Where(l => !Regex.IsMatch(l, "^\\s*mod\\s+\"CrusaderConflicts\\.pack\"", RegexOptions.IgnoreCase))) + "\n";
        File.WriteAllText(Path.Combine(output, "used_mods_cc_calibration.txt"), startup);
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new {
            kind = "active_stack_proportional_mounted_and_real_knight_probe", profilesFile, sourcePacks = database.SourcePacks,
            subjects = profiles.Select((p, i) => new { scriptName = subjects[i], requestedMen = counts[i], profile = p, generatedKey = keys[p.Id], source = database.Query(p.BaseUnitKey) }),
            expectedCards = profiles.Select((p, i) => {
                var source = database.Query(p.BaseUnitKey);
                return new { subject = subjects[i],
                    damage = checked((int)Math.Round(source.MeleeDamage * p.DamageMultiplier, MidpointRounding.AwayFromZero) +
                                     (int)Math.Round(source.ArmourPiercingDamage * p.DamageMultiplier, MidpointRounding.AwayFromZero)),
                    health = checked((int)Math.Round(source.EffectiveHitPoints!.Value * p.HitPointMultiplier, MidpointRounding.AwayFromZero)) };
            }),
            confidence = "Full active pack stack used by production backend; binary DB fields roundtrip verified. Native battle preserved except three ordinary unit types/counts/XP. Expected cards are predictions; root must verify actual engine cards. No outcome-equivalence claim.",
            xmlEncoding = encoding.WebName, automaticLaunch
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Prepared active mounted probe; all generated fields roundtrip verified. No game launched.");
    }
}
