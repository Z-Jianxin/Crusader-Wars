using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrusaderWars.StatFidelity;
using MoonSharp.Interpreter;

internal static class NativeUnitSmoke
{
    public static void Prepare(string rpfm, string schema, string game, string output,
        AttilaDatabase database, bool replaceOneUnit, bool compactIndex, bool countProbe = false, bool automaticLaunch = false)
    {
        string native = Path.Combine(output, "native-source"); Directory.CreateDirectory(native);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", Path.Combine(game, "data", "data.pack"),
            "-f", "lua_scripts/frontend_hbs.lua;" + native,
            "-F", "script/an_adrianople;" + native);
        string nativeXml;
        Encoding nativeXmlEncoding;
        using (var reader = new StreamReader(Path.Combine(native, "script", "an_adrianople", "an_battle.xml"), Encoding.UTF8, true))
        {
            nativeXml = reader.ReadToEnd();
            nativeXmlEncoding = reader.CurrentEncoding;
        }
        var unit = XDocument.Parse(nativeXml).Descendants("unit").Single(x => (string?)x.Attribute("script_name") == "Vgt_02");
        string baseKey = (string)unit.Element("unit_type")!.Attribute("type")!;
        if (unit.Element("general") != null) throw new InvalidDataException("Smoke subject unexpectedly became the general.");
        var builder = new StatVariantBuilder(database);
        string generatedKey = builder.Add(new("native-smoke-one-unit", baseKey, 2, 1));
        var tables = builder.Tables.ToList();
        var main = tables.Single(t => t.Name == "main_units_tables").Rows.Single();
        string originalGeneratedIndex = main["unique_index"];
        if (compactIndex)
        {
            var reserved = database.Tables["main_units_tables"].Rows.Select(r => long.Parse(r["unique_index"], CultureInfo.InvariantCulture)).ToHashSet();
            int next = 1;
            while (reserved.Contains(next)) next = checked(next + 1);
            main["unique_index"] = next.ToString(CultureInfo.InvariantCulture);
        }
        string payload = Path.Combine(output, "payload"); Directory.CreateDirectory(payload);
        foreach (var table in tables)
            table.Write(table.Name == "Loc" ? Path.Combine(payload, "text", "db", "cc_native_smoke.loc.tsv")
                : Path.Combine(payload, "db", table.Name, "cc_native_smoke.tsv"));
        string scripts = Path.Combine(payload, "script", "an_adrianople"); Directory.CreateDirectory(scripts);
        if (replaceOneUnit)
        {
            string pattern = "(<unit script_name=\"Vgt_02\">\\s*<unit_type type=\")" + Regex.Escape(baseKey) + "(\"/>)";
            if (Regex.Matches(nativeXml, pattern).Count != 1) throw new InvalidDataException("Expected exactly one native subject unit_type.");
            string changed = Regex.Replace(nativeXml, pattern, m => m.Groups[1].Value + generatedKey + m.Groups[2].Value);
            if (countProbe)
            {
                changed = changed.Replace("<unit script_name=\"Vgt_02\">", "<unit script_name=\"Vgt_02\" num_soldiers=\"20\">");
                string otherPattern = "<unit script_name=\"Vgt_04\">.*?</unit>";
                var other = Regex.Matches(changed, otherPattern, RegexOptions.Singleline);
                if (other.Count != 1) throw new InvalidDataException("Count probe needs exactly one Vgt_04.");
                string otherChanged = other[0].Value.Replace("<unit script_name=\"Vgt_04\">", "<unit script_name=\"Vgt_04\" num_soldiers=\"500\">");
                otherChanged = Regex.Replace(otherChanged, "(<unit_type\\s+type=\")[^\"]+(\")", m => m.Groups[1].Value + generatedKey + m.Groups[2].Value);
                changed = changed.Replace(other[0].Value, otherChanged);
            }
            var changedUnit = XDocument.Parse(changed).Descendants("unit").Single(x => (string?)x.Attribute("script_name") == "Vgt_02");
            changedUnit.Element("unit_type")!.SetAttributeValue("type", baseKey);
            if (countProbe) changedUnit.Attribute("num_soldiers")!.Remove();
            if (!XNode.DeepEquals(unit, changedUnit)) throw new InvalidDataException("Smoke fixture changed more than subject unit type.");
            File.WriteAllText(Path.Combine(scripts, "an_battle.xml"), changed, nativeXmlEncoding);
        }
        string logPath = Path.Combine(output, "native-smoke.log").Replace('\\', '/').Replace("\"", "\\\"");
        string logger = "function cc_native_smoke_log(message)\n" +
            "    local f = io.open(\"" + logPath + "\", \"a\")\n" +
            "    if f then f:write(tostring(os.time()) .. \" \" .. tostring(message) .. \"\\n\"); f:close() end\nend\n";
        string frontend = logger + "cc_native_smoke_log(\"frontend loaded\")\n" +
            File.ReadAllText(Path.Combine(native, "lua_scripts", "frontend_hbs.lua")).Replace(
                "AddEventCallBack(\"HistoricBattleEvent\", OnHistoricBattleEvent);",
                "AddEventCallBack(\"HistoricBattleEvent\", function(context) cc_native_smoke_log(\"HistoricBattleEvent: \" .. tostring(context.string)); OnHistoricBattleEvent(context) end);");
        string frontendDir = Path.Combine(payload, "lua_scripts"); Directory.CreateDirectory(frontendDir);
        if (automaticLaunch) frontend += HistoricalAutostart.Script("cc_native_smoke_log");
        new Script().LoadString(frontend);
        File.WriteAllText(Path.Combine(frontendDir, "frontend_hbs.lua"), frontend, new UTF8Encoding(false));
        string battle = logger + "cc_native_smoke_log(\"native battle script loaded\")\n" +
            File.ReadAllText(Path.Combine(native, "script", "an_adrianople", "an_start.lua")) + """

cc_native_smoke_log("native setup finished")
cc_native_smoke_log("Vgt_02 type=" .. SUnit_Vgt_02.unit:type() .. " men=" .. SUnit_Vgt_02.unit:number_of_men_alive())
for _, phase in ipairs({ "Deployment", "Deployed", "VictoryCountdown", "Complete" }) do
    local label = phase
    bm:register_phase_change_callback(phase, function() cc_native_smoke_log("PHASE " .. label) end)
end

""";
        if (countProbe)
            battle += "\ncc_native_smoke_log(\"Vgt_04 type=\" .. SUnit_Vgt_04.unit:type() .. \" men=\" .. SUnit_Vgt_04.unit:number_of_men_alive())\n";
        new Script().LoadString(battle);
        File.WriteAllText(Path.Combine(scripts, "an_start.lua"), battle, new UTF8Encoding(false));
        string pack = Path.Combine(output, "cc_stat_calibration.pack");
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", pack);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", pack, "-t", schema, "-F", payload);
        string extracted = Path.Combine(output, "roundtrip"); Directory.CreateDirectory(extracted);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", pack, "-t", schema, "-F", "db;" + extracted, "-F", "text;" + extracted);
        foreach (var table in tables)
        {
            string path = table.Name == "Loc" ? Path.Combine(extracted, "text", "db", "cc_native_smoke.loc.tsv")
                : Path.Combine(extracted, "db", table.Name, "cc_native_smoke.tsv");
            var actual = TsvTable.Read(path);
            if (actual.Version != table.Version || actual.Rows.Count != table.Rows.Count)
                throw new InvalidDataException("Smoke pack roundtrip changed " + table.Name);
            for (int i = 0; i < table.Rows.Count; i++)
                foreach (string column in table.Columns)
                    if (actual.Rows[i][column] != table.Rows[i][column])
                        throw new InvalidDataException("Smoke pack roundtrip changed " + table.Name + "." + column);
        }
        File.WriteAllText(Path.Combine(output, "used_mods_cc_calibration.txt"),
            $"show_frontend_movies false;\nadd_working_directory \"{output.Replace('\\', '/')}\";\nmod \"cc_stat_calibration.pack\";\n");
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            kind = countProbe ? "native_representation_count_probe" : "native_one_unit_smoke", replacedUnitCount = countProbe ? 2 : replaceOneUnit ? 1 : 0, compactIndex, baseKey, generatedKey, subject = "Vgt_02",
            requestedCounts = countProbe ? new Dictionary<string, int> { ["Vgt_02"] = 20, ["Vgt_04"] = 500 } : null,
            nominalSoldiers = main["num_men"],
            damageMultiplier = 2, hitPointMultiplier = 1, uniqueIndex = main["unique_index"], originalGeneratedIndex,
            battle = "Adrianople", launch = automaticLaunch ? "automatic_historical_after_uicreated" : "manual_historical_battle_menu", xmlEncoding = nativeXmlEncoding.WebName,
            generatedStatRecords = tables.ToDictionary(t => t.Name, t => t.Rows),
            confidence = countProbe ? "Native scenario with two unit_type/count changes; native gameplay retained. Requested counts require actual engine log verification."
                : "Native XML unchanged or only Vgt_02 unit_type replaced; native script and gameplay retained. Generated DB binary roundtrip verified. Engine behavior pending."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Prepared native smoke: replaceOneUnit={replaceOneUnit}; compactIndex={compactIndex}; unit={baseKey}; index={main["unique_index"]}");
        Console.WriteLine(automaticLaunch ? "Automatic native historical launch is staged; no game was launched." : "No game launched. Use Historical Battles > Adrianople.");
    }
}
