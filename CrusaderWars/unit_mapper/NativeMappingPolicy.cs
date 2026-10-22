using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using CrusaderWars.StatFidelity;

namespace CrusaderWars.unit_mapper;

public static class NativeMappingPolicy
{
    public static string? CohortCategory(string? nativeCategory) => nativeCategory switch {
        "heavy_infantry" or "pikemen" or "skirmishers" or "peasant_militia" => "INFANTRY",
        "archers" => "RANGED",
        "heavy_cavalry" or "light_cavalry" or "archer_cavalry" => "CAVALRY",
        _ => null
    };

    // This deliberately does not invent elephant, camel, gunpowder, nomadic-horde
    // or combat-artillery population/weapon contracts from a generic max category.
    public static string? RoleMismatch(string category, AttilaUnitSemanticStats unit, string? nativeType = null)
    {
        if (CohortCategory(category) == null) return "unsupported-native-family:" + category;
        if (unit.IsNaval || unit.EngineKey.Length > 0) return "ship-or-combat-artillery";
        bool foot = !unit.IsMounted && !unit.HasSpecialEntity && unit.Category is "inf_melee" or "inf_ranged";
        bool horse = unit.IsMounted && unit.Category == "cavalry" && unit.MountEntityType == "horse";
        bool ranged = unit.HasMissileWeapon && unit.MissilePrecursor == false && unit.Ammunition > 0;
        return category switch {
            "heavy_infantry" or "pikemen" or "skirmishers" or "peasant_militia" => foot ? null : "foot-native-with-nonfoot-template",
            "archers" => foot && ranged ? null : "foot-archers-without-sustained-missile",
            "heavy_cavalry" => horse && (unit.Weight is "heavy" or "very_heavy" or "super_heavy") &&
                (nativeType == "cataphract_archers" ? ranged : !unit.HasMissileWeapon || unit.MissilePrecursor == true)
                ? null : "heavy-cavalry-with-incompatible-horse-template",
            "light_cavalry" => horse && unit.Weight is "very_light" or "light" or "medium" ? null : "light-cavalry-with-nonlight-horse-template",
            "archer_cavalry" => horse && ranged ? null : "horse-archers-without-mounted-missile-template",
            _ => "unsupported-native-family:" + category
        };
    }

    public static string? RepresentationIssue(AttilaUnitSemanticStats unit)
    {
        if (unit.MissingReferences.Count > 0) return "missing-active-references:" + string.Join(",", unit.MissingReferences);
        if (!unit.HasVisualVariant) return "missing-active-visual-variant";
        if (unit.NominalSoldiers <= 0 || !unit.CanScaleHitPoints || unit.EffectiveHitPoints is not > 0)
            return "unsupported-body-or-hitpoint-representation";
        return null;
    }

    /// <summary>Later partial defaults replace only their authored type or whole role pool.</summary>
    public static XmlNode MergeDefaultBlocks(IEnumerable<XmlNode> blocksLowToHigh)
    {
        var result = new XmlDocument(); result.LoadXml("<Faction name='Default'/>");
        var root = result.DocumentElement!;
        foreach (var block in blocksLowToHigh)
        {
            var incoming = block.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().ToArray();
            foreach (string role in incoming.Where(n => n.Name != "MenAtArm").Select(n => n.Name).Distinct())
                foreach (var old in root.ChildNodes.Cast<XmlNode>().Where(n => n.Name == role).ToArray()) root.RemoveChild(old);
            foreach (var node in incoming)
            {
                if (node.Name == "MenAtArm")
                    foreach (var old in root.ChildNodes.Cast<XmlNode>().OfType<XmlElement>()
                        .Where(n => n.Name == "MenAtArm" && n.GetAttribute("type") == node.GetAttribute("type")).ToArray()) root.RemoveChild(old);
                root.AppendChild(result.ImportNode(node, true));
            }
        }
        return root;
    }
}
