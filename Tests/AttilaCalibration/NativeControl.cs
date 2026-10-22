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
        string nativeBattle = File.ReadAllText(battlePath);
        string battle = logger + "cc_native_control_log(\"native Adrianople battle script loaded\")\n" + """
function cc_native_control_guard(label, callback)
    cc_native_control_log("ENTER " .. label)
    local ok, result = xpcall(callback, function(err) return debug.traceback(tostring(err), 2) end)
    if not ok then
        cc_native_control_log("ERROR " .. label .. ": " .. tostring(result))
        error(result)
    end
    cc_native_control_log("EXIT " .. label)
    return result
end
cc_native_control_guard("native setup", function()

""" + nativeBattle + """

end)
cc_native_control_log("native Adrianople battle script setup finished")
for _, name in ipairs({ "Deployment_Phase", "End_Deployment_Phase", "Play_Cutscene_Intro", "Start_Battle" }) do
    local original = _G[name]
    local label = name
    if type(original) == "function" then
        _G[name] = function() return cc_native_control_guard(label, original) end
    end
end
for _, phase in ipairs({ "Deployment", "Deployed", "VictoryCountdown", "Complete" }) do
    local label = phase
    bm:register_phase_change_callback(phase, function() cc_native_control_log("PHASE " .. label) end)
end
bm:repeat_callback(function() cc_native_control_log("battle timer tick") end, 5000, "cc_native_control_heartbeat")

""";
        new Script().LoadString(battle);
        File.WriteAllText(battlePath, battle, new UTF8Encoding(false));
        string packPath = Path.Combine(output, "cc_stat_calibration.pack");
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", packPath);
        RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", packPath, "-t", schema, "-F", payload);
        string listing = RpfmRunner.Run(rpfm, "--game", "attila", "pack", "list", "-p", packPath);
        if (!listing.Contains("an_start.lua") || !listing.Contains("frontend_hbs.lua") || listing.Contains("db/"))
            throw new InvalidDataException("Native control must contain only its two instrumented native scripts.");
        File.WriteAllText(Path.Combine(output, "used_mods_cc_calibration.txt"),
            $"show_frontend_movies false;\nadd_working_directory \"{output.Replace('\\', '/')}\";\nmod \"cc_stat_calibration.pack\";\n");
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
