using System.Text;
using System.Text.Json;
using CrusaderWars.StatFidelity;
using MoonSharp.Interpreter;

/// <summary>Zero-stat control: use Attila's own menu, historical DB, XML, units and map.</summary>
internal static class NativeControl
{
    public static void Prepare(string rpfm, string schema, string game, string output)
    {
        string payload = Path.Combine(output, "payload");
        Directory.CreateDirectory(payload);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", Path.Combine(game, "data", "data.pack"),
            "-f", "lua_scripts/frontend_hbs.lua;" + payload,
            "-f", "script/an_adrianople/an_start.lua;" + payload);
        string logPath = Path.Combine(output, "native-control.log").Replace('\\', '/');
        string logger = "function cc_native_control_log(message)\n" +
            "    local f = io.open(\"" + logPath.Replace("\"", "\\\"") + "\", \"a\")\n" +
            "    if f then f:write(tostring(os.time()) .. \" \" .. tostring(message) .. \"\\n\"); f:close() end\nend\n";
        string frontPath = Path.Combine(payload, "lua_scripts", "frontend_hbs.lua");
        string frontend = File.ReadAllText(frontPath);
        const string registration = "AddEventCallBack(\"HistoricBattleEvent\", OnHistoricBattleEvent);";
        if (!frontend.Contains(registration)) throw new InvalidDataException("Native historical event registration changed.");
        frontend = logger + "cc_native_control_log(\"native frontend module loaded; manual launch only\")\n" +
            frontend.Replace(registration, "AddEventCallBack(\"HistoricBattleEvent\", function(context)\n" +
                "    cc_native_control_log(\"HistoricBattleEvent: \" .. tostring(context.string))\n" +
                "    OnHistoricBattleEvent(context)\nend);") +
            "\nAddEventCallBack(\"UICreated\", function(context) cc_native_control_log(\"UICreated\") end);\n" +
            "cc_native_control_log(\"native frontend module finished\")\n";
        new Script().LoadString(frontend);
        File.WriteAllText(frontPath, frontend, new UTF8Encoding(false));
        string battlePath = Path.Combine(payload, "script", "an_adrianople", "an_start.lua");
        string battle = logger + "cc_native_control_log(\"native Adrianople battle script loaded\")\n" + File.ReadAllText(battlePath);
        new Script().LoadString(battle);
        File.WriteAllText(battlePath, battle, new UTF8Encoding(false));
        string packPath = Path.Combine(output, "cc_stat_calibration.pack");
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", packPath);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", packPath, "-t", schema, "-F", payload);
        string listing = RpfmRunner.Run(rpfm, "--game", "attila", "pack", "list", "-p", packPath);
        if (!listing.Contains("an_start.lua") || !listing.Contains("frontend_hbs.lua") || listing.Contains("db/"))
            throw new InvalidDataException("Native control must contain only its two instrumented native scripts.");
        File.WriteAllText(Path.Combine(output, "used_mods_cc_calibration.txt"),
            $"add_working_directory \"{output.Replace('\\', '/')}\";\nmod \"cc_stat_calibration.pack\";\n");
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            kind = "native_historical_control", battle = "Adrianople", launch = "manual_historical_battle_menu",
            sourcePack = Path.Combine(game, "data", "data.pack"), customStatRecords = 0,
            modifiedVirtualFiles = new[] { "lua_scripts/frontend_hbs.lua", "script/an_adrianople/an_start.lua" },
            confidence = "Native scripts compiled; logging only. No automatic launch, DB, XML, terrain, unit or stat changes. Engine execution pending."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Prepared native Adrianople control: " + packPath);
        Console.WriteLine("Launch with the task mod-list, then select Historical Battles > Adrianople using the normal menu. No game was launched.");
    }
}
