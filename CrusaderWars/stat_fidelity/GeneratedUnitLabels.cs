using CrusaderWars.data.save_file;
using CrusaderWars.client;
using CrusaderWars.locs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CrusaderWars.StatFidelity;

/// <summary>Names the generated formation, rather than every user of its shared base asset.</summary>
public static class GeneratedUnitLabels
{
    private static readonly Dictionary<string, string> labels = new(StringComparer.Ordinal);
    public static IReadOnlyDictionary<string, string> Pending => new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(labels);

    public static void Capture(IEnumerable<Army> sourceArmies, string battleXml)
    {
        labels.Clear();
        if (!StatFidelitySession.HasBoundExport || !ModOptions.UnitCards()) return;
        var armies = new List<Army>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(Army army)
        {
            if (!seen.Add(army.ID)) return;
            armies.Add(army);
            foreach (var merged in army.MergedArmies ?? new List<Army>()) Visit(merged);
        }
        foreach (var army in sourceArmies) Visit(army);
        var units = armies.SelectMany(army => army.Units.Select(unit => (army, unit))).ToDictionary(pair => pair.unit.UniqueID);
        var requested = units.Values.Where(pair => pair.unit.GetRegimentType() == RegimentType.MenAtArms)
            .Select(pair => pair.unit.GetName()).Concat(units.Values.Where(pair => pair.unit.CommanderGuardRepresentation != null)
                .Select(pair => pair.unit.CommanderGuardRepresentation!.DonorTypeName)).ToArray();
        string? installation = Path.GetDirectoryName(Path.GetDirectoryName(Properties.Settings.Default.VAR_ck3_path));
        var roots = new List<string>();
        if (installation != null) roots.Add(Path.Combine(installation, "game", "localization", "english"));
        roots.AddRange(LandedTitles.GetEnabledModsPaths().Select(path => Path.Combine(path, "localization", "english")));
        var names = Ck3RegimentNames.Resolve(roots, requested);
        foreach (var element in XDocument.Load(battleXml).Descendants("unit"))
        {
            string key = element.Element("unit_type")?.Attribute("type")?.Value ?? "";
            if (!key.StartsWith("cc_sf_", StringComparison.Ordinal)) continue;
            string script = element.Attribute("script_name")?.Value ?? "";
            var identity = Regex.Match(script, @"_(?<unit>\d+)_CULTURE", RegexOptions.CultureInvariant);
            if (!identity.Success || !int.TryParse(identity.Groups["unit"].Value, out int id) || !units.TryGetValue(id, out var pair))
                throw new InvalidDataException("A private unit label has no exact generated formation identity: " + script);
            string? label = null;
            if (pair.unit.CommanderGuardRepresentation is { } guard)
            {
                string donorName = guard.DonorType == RegimentType.Levy ? "Levy" : names.GetValueOrDefault(guard.DonorTypeName, Ck3RegimentNames.Fallback(guard.DonorTypeName));
                label = "Commander's guard (" + donorName + ")";
            }
            else if (pair.unit.GetRegimentType() == RegimentType.MenAtArms)
                label = "MAA " + names.GetValueOrDefault(pair.unit.GetName(), Ck3RegimentNames.Fallback(pair.unit.GetName()));
            else if (pair.unit.GetRegimentType() == RegimentType.Knight)
                label = UnitsCardsNames.GetFormattedUnitName(pair.unit, pair.army);
            if (label == null) continue;
            if (labels.TryGetValue(key, out string? previous) && previous != label)
                throw new InvalidDataException("A generated unit key has conflicting CK3 identities for its card label: " + key);
            labels[key] = label;
        }
    }

    public static void Apply(string battleRoot)
    {
        if (!StatFidelitySession.HasBoundExport || !ModOptions.UnitCards() || labels.Count == 0) return;
        string path = Path.Combine(battleRoot, "text", "db", "cc_stat_fidelity.loc.tsv");
        var table = TsvTable.Read(path);
        foreach (var pair in labels)
        {
            var rows = table.Rows.Where(row => row["key"] == "land_units_onscreen_name_" + pair.Key).ToArray();
            if (rows.Length != 1) throw new InvalidDataException("Missing or duplicate private unit-card localization: " + pair.Key);
            rows[0]["text"] = pair.Value;
        }
        table.Write(path);
    }
}
