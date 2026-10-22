using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity;

public sealed record AttilaSiegeUnitStats(string UnitKey, string LandUnitKey, string EngineKey, int NominalCrew, int NominalEngines)
{
    internal static AttilaSiegeUnitStats Read(AttilaDatabase database, string unitKey)
    {
        var main = database.Require("main_units_tables", unitKey);
        var land = database.Require("land_units_tables", main["land_unit"]);
        string engine = land.GetValueOrDefault("engine") ?? "";
        int crew = int.Parse(main["num_men"], CultureInfo.InvariantCulture);
        int guns = int.Parse(land.GetValueOrDefault("num_guns", "0"), CultureInfo.InvariantCulture);
        if (main.GetValueOrDefault("is_naval") == "true" || land.GetValueOrDefault("category") != "artillery"
            || string.IsNullOrWhiteSpace(engine) || crew <= 0 || guns <= 0 || crew % guns != 0
            || new[] { "mount", "animal", "chariot", "articulated_record" }.Any(field => !string.IsNullOrWhiteSpace(land.GetValueOrDefault(field))))
            throw new InvalidDataException($"Siege mapping '{unitKey}' is not supported artillery with an exact active-pack crew/engine ratio. Repair the mapper; infantry proxies and guessed crew counts cannot be generated.");
        database.Require("battlefield_engines_tables", engine);
        return new(unitKey, main["land_unit"], engine, crew, guns);
    }
}

public sealed record SiegeBattery(int SourceMachines, int AttilaEngines, int Crew);

public static class SiegeBatteryPlanner
{
    // The exact-report validator permits at most 4096 formations for the whole battle.
    public const int MaximumBatteries = 4096;

    public static IReadOnlyList<SiegeBattery> Plan(int sourceMachines, int attilaEngines, AttilaSiegeUnitStats template)
    {
        if (sourceMachines <= 0 || attilaEngines <= 0 || attilaEngines > sourceMachines)
            throw new InvalidDataException("Siege representation needs positive native/scaled machine counts; battlefield scaling may not drop a source or invent additional machines.");
        if (template.NominalCrew <= 0 || template.NominalEngines <= 0 || template.NominalCrew % template.NominalEngines != 0)
            throw new InvalidDataException("Active artillery crew/engine ratio must be exact and positive.");
        int count = checked((attilaEngines - 1) / template.NominalEngines + 1);
        if (count > MaximumBatteries)
            throw new InvalidDataException("Siege source exceeds the 4096-formation exact-report limit. No source machines were discarded; reduce battlefield scale.");
        int crewPerEngine = template.NominalCrew / template.NominalEngines;
        var engines = Enumerable.Range(0, count).Select(i => Math.Min(template.NominalEngines, attilaEngines - i * template.NominalEngines)).ToArray();
        var native = new int[count];
        var remainders = new long[count];
        long allocated = 0;
        for (int i = 0; i < count; i++)
        {
            long product = checked((long)sourceMachines * engines[i]);
            native[i] = checked((int)(product / attilaEngines));
            remainders[i] = product % attilaEngines;
            allocated += native[i];
        }
        int remaining = checked(sourceMachines - (int)allocated);
        foreach (int index in Enumerable.Range(0, count).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take(remaining)) native[index]++;
        return Array.AsReadOnly(engines.Select((guns, i) => new SiegeBattery(native[i], guns, checked(guns * crewPerEngine))).ToArray());
    }
}
