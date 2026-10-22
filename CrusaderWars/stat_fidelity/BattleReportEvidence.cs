using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity;

public sealed record DeployedFormation(string ScriptName, int InitialSoldiers);

/// <summary>Complete, immutable native result phases bound to the exact deployed script names.</summary>
public sealed class ValidatedBattleReports
{
    public IReadOnlyList<IReadOnlyDictionary<string, int>> SurvivorPhases { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, int>> KillPhases { get; }
    public IReadOnlyDictionary<string, int> FinalSurvivors => SurvivorPhases[^1];
    public IReadOnlyDictionary<string, int>? FinalKills => KillPhases.Count == 0 ? null : KillPhases[^1];

    internal ValidatedBattleReports(List<IReadOnlyDictionary<string, int>> survivors,
        List<IReadOnlyDictionary<string, int>> kills)
    {
        SurvivorPhases = Array.AsReadOnly(survivors.ToArray());
        KillPhases = Array.AsReadOnly(kills.ToArray());
    }
}

public static class BattleReportEvidence
{
    public const int MaximumFormations = 4096;
    public const int MaximumPhases = 32;
    public const int MaximumScriptNameLength = 512;
    public const int MaximumBlockCharacters = 4 * 1024 * 1024;
    public const int MaximumTotalCharacters = 16 * 1024 * 1024;

    /// <summary>
    /// Blocks contain only name-count rows, without phase headers/footers. Blank lines are allowed.
    /// Null killBlocks means absent kill evidence; a supplied list must match every survivor phase.
    /// Names are matched ordinally, without parsing culture, side, regiment or character tokens.
    /// </summary>
    public static ValidatedBattleReports Validate(IReadOnlyList<DeployedFormation> roster,
        IReadOnlyList<string> survivorBlocks, IReadOnlyList<string>? killBlocks = null)
    {
        if (roster == null || roster.Count == 0 || roster.Count > MaximumFormations)
            throw new InvalidDataException("Battle report requires a bounded, nonempty deployed roster.");
        var deployed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var formation in roster)
        {
            if (formation == null || string.IsNullOrWhiteSpace(formation.ScriptName) ||
                formation.ScriptName.Length > MaximumScriptNameLength ||
                formation.ScriptName != formation.ScriptName.Trim() || formation.ScriptName.Any(char.IsControl) ||
                formation.InitialSoldiers < 0)
                throw new InvalidDataException("Invalid deployed formation identity or soldier count.");
            if (!deployed.TryAdd(formation.ScriptName, formation.InitialSoldiers))
                throw new InvalidDataException("Duplicate deployed formation: " + formation.ScriptName);
        }
        if (survivorBlocks == null || survivorBlocks.Count == 0 || survivorBlocks.Count > MaximumPhases)
            throw new InvalidDataException("Battle report requires a bounded, nonempty list of survivor phases.");
        if (killBlocks != null && killBlocks.Count != survivorBlocks.Count)
            throw new InvalidDataException("Kill phases must match every survivor phase when supplied.");

        long characters = 0;
        foreach (string? block in survivorBlocks.Concat(killBlocks ?? Array.Empty<string>()))
        {
            if (block == null || block.Length == 0 || block.Length > MaximumBlockCharacters)
                throw new InvalidDataException("Missing, empty or oversized battle report phase.");
            characters += block.Length;
            if (characters > MaximumTotalCharacters)
                throw new InvalidDataException("Battle report exceeds the total input size limit.");
        }

        var survivors = new List<IReadOnlyDictionary<string, int>>();
        var kills = new List<IReadOnlyDictionary<string, int>>();
        IReadOnlyDictionary<string, int> previous = deployed;
        for (int phase = 0; phase < survivorBlocks.Count; phase++)
        {
            var counts = ParsePhase(survivorBlocks[phase], deployed, isKills: false);
            foreach (var count in counts)
                if (count.Value > previous[count.Key])
                    throw new InvalidDataException($"Survivors exceed deployed or previous-phase count for {count.Key} in phase {phase + 1}.");
            survivors.Add(counts);
            previous = counts;
            if (killBlocks != null) kills.Add(ParsePhase(killBlocks[phase], deployed, isKills: true));
        }
        return new ValidatedBattleReports(survivors, kills);
    }

    private static IReadOnlyDictionary<string, int> ParsePhase(string block,
        IReadOnlyDictionary<string, int> deployed, bool isKills)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = new StringReader(block);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > MaximumScriptNameLength + 17)
                throw new InvalidDataException("Oversized battle report record.");
            if (isKills)
            {
                if (!line.StartsWith("kills_", StringComparison.Ordinal))
                    throw new InvalidDataException("Kill report record lacks its kills_ prefix.");
                line = line[6..];
            }
            int separator = line.LastIndexOf('-');
            if (separator <= 0 || separator == line.Length - 1)
                throw new InvalidDataException("Malformed battle report record.");
            string name = line[..separator];
            if (!deployed.ContainsKey(name))
                throw new InvalidDataException("Unknown battle report formation: " + name);
            if (!int.TryParse(line.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException("Invalid nonnegative integer count for formation " + name);
            if (!counts.TryAdd(name, value))
                throw new InvalidDataException("Duplicate battle report formation: " + name);
        }
        if (counts.Count != deployed.Count)
            throw new InvalidDataException("Battle report phase does not contain exactly every deployed formation.");
        return new ReadOnlyDictionary<string, int>(counts);
    }
}
