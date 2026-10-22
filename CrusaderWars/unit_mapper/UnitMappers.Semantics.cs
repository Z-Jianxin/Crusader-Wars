using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using CrusaderWars.twbattle;

namespace CrusaderWars.unit_mapper;

public sealed record NativeMappingDecision(string Status, string NativeType, string? NativeCategory,
    string OriginalKey, string ResolvedKey, string Reason, string? CandidateNativeType = null, string? CandidateFile = null);

internal static partial class UnitMappers_BETA
{
    private sealed record AuthoredCandidate(string Type, string Key, string? Maximum, string File);
    private static readonly Dictionary<string, List<AuthoredCandidate>> nativeCandidatePools = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> reviewedSemanticFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AttilaUnitSemanticStats?> semanticAssets = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, NativeMappingDecision> nativeMappingDecisions = new();
    private static PreparedAttilaDatabase? semanticDatabase;
    private static string? nativePoolContext;
    public static IReadOnlyDictionary<int, NativeMappingDecision> NativeMappingDecisions => nativeMappingDecisions;
    private static bool NativeSemanticPolicyEnabled => ActivePlaythroughTag == "DefaultCK3" && StatFidelitySession.HasBoundExport && StatFidelitySession.PreparedDatabase != null;
    private static void ClearSemanticMappings()
    {
        InvalidateNativeCandidatePools(); nativePoolContext = null; semanticAssets.Clear(); nativeMappingDecisions.Clear(); semanticDatabase = null;
    }
    private static void InvalidateNativeCandidatePools() { nativeCandidatePools.Clear(); reviewedSemanticFiles.Clear(); }

    private static bool ReviewedSemanticFile(string file)
    {
        if (reviewedSemanticFiles.TryGetValue(file, out bool known)) return known;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        bool official = Path.GetFileName(file) switch {
            "OfficialCC_DefaultCK3_EarlyMedieval_Factions.xml" => hash == "87037C7C5D28517840C32470893B7DC87D3552974BC76AEF1FAC9427185B03EC",
            "OfficialCC_DefaultCK3_HighMedieval_Factions.xml" => hash == "975D6102FD18F05C607BB2044AE6AF40BEE130464A86201F720C693EADAF3D96",
            "OfficialCC_DefaultCK3_LateMedieval_Factions.xml" => hash == "FC4826AD005B2A71D47F0F0BD592A1C226D6230DED565A3F94B8570374245ACB",
            "OfficialCC_DefaultCK3_Renaissance_Factions.xml" => hash == "6938FAC16D6515672AA872F775DED0CCA39C77FA4B45E2CD6F2D08D70855E653",
            _ => false
        };
        var scope = DefaultCk3OverlayScope.Identify(ActivePlaythroughTag, Path.GetFileName(file), hash);
        return reviewedSemanticFiles[file] = official || scope is MapperOverlayScope.Asian or MapperOverlayScope.Additive;
    }

    private static List<AuthoredCandidate> NativeCandidatePool(string faction)
    {
        var result = new List<AuthoredCandidate>(); var types = new HashSet<string>(StringComparer.Ordinal);
        if (LoadedUnitMapper_FolderPath == null) return result;
        string folder = Path.Combine(LoadedUnitMapper_FolderPath, "Factions");
        string context = ActivePlaythroughTag + "\n" + Path.GetFullPath(folder) + "\n" +
            string.Join("\n", CrusaderWars.mod_manager.SubmodManager.GetActiveSubmodsForPlaythrough(ActivePlaythroughTag ?? "").OrderBy(t => t, StringComparer.Ordinal)) + "\n" +
            string.Join("\n", Directory.GetFiles(folder, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => { var f = new FileInfo(p); return p + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks; }));
        if (context != nativePoolContext) { InvalidateNativeCandidatePools(); nativePoolContext = context; }
        if (nativeCandidatePools.TryGetValue(faction, out var cached)) return cached;
        var files = GetSortedFilePaths(Path.Combine(LoadedUnitMapper_FolderPath, "Factions"), "OfficialCC_DefaultCK3_*"); files.Reverse();
        foreach (string file in files)
        {
            var document = ReadMapperXml(file);
            var block = document.DocumentElement?.ChildNodes.Cast<XmlNode>().OfType<XmlElement>()
                .FirstOrDefault(n => n.Name == "Faction" && (n.GetAttribute("name") == faction || faction == "Default" && n.GetAttribute("name") == "DEFAULT"));
            if (block == null) continue;
            foreach (var node in block.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().Where(n => n.Name == "MenAtArm"))
            {
                string type = node.GetAttribute("type"), key = node.GetAttribute("key");
                if (type.Length > 0 && key.Length > 0 && types.Add(type)) result.Add(new(type, key, node.GetAttribute("max"), file));
            }
        }
        return nativeCandidatePools[faction] = result;
    }

    private static AttilaUnitSemanticStats? SemanticAsset(string key)
    {
        var prepared = StatFidelitySession.PreparedDatabase!;
        if (!ReferenceEquals(prepared, semanticDatabase)) { semanticAssets.Clear(); semanticDatabase = prepared; }
        if (semanticAssets.TryGetValue(key, out var cached)) return cached;
        try { return semanticAssets[key] = prepared.QuerySemanticUnit(key); }
        catch (InvalidOperationException) { return semanticAssets[key] = null; }
    }

    private static AuthoredCandidate? FindCompatibleAuthoredCandidate(Unit unit, NativeMaaDefinition native, string faction, string? exclude = null)
    {
        return NativeCandidatePool(faction).Where(candidate => candidate.Key != exclude)
            .Select(candidate => (candidate, definition: ArmiesReader.TryGetNativeMaaDefinition(candidate.Type, out var definition) ? definition : null))
            .Where(row => row.definition?.Category == native.Category)
            .OrderBy(row => row.candidate.Type == native.Key ? 0 : Path.GetFileName(row.definition!.SourceFile) == "00_maa_types.txt" ? 1 : 2)
            .ThenBy(row => row.candidate.Type, StringComparer.Ordinal).ThenBy(row => row.candidate.Key, StringComparer.Ordinal)
            .Select(row => row.candidate).FirstOrDefault(candidate => SemanticAsset(candidate.Key) is { } asset &&
                NativeMappingPolicy.RoleMismatch(native.Category ?? "", asset, native.Key) == null && NativeMappingPolicy.RepresentationIssue(asset) == null);
    }

    private static (string unit_key, bool isSiege) ResolveNativeSemanticMapping(Unit unit, (string unit_key, bool isSiege) mapped, bool title, bool genericFallback)
    {
        if (!NativeSemanticPolicyEnabled || unit.GetRegimentType() != RegimentType.MenAtArms || !ArmiesReader.TryGetNativeMaaDefinition(unit.GetName(), out var native)) return mapped;
        void Record(string status, string key, string reason, AuthoredCandidate? candidate = null) =>
            nativeMappingDecisions[unit.UniqueID] = new(status, native.Key, native.Category, mapped.unit_key, key, reason, candidate?.Type, candidate?.File);
        (string, bool) Unsupported(string reason)
        {
            Record("unsupported", NOT_FOUND_KEY, reason);
            throw new InvalidOperationException("Unsupported CK3/Attila mapping " + unit.GetAttilaFaction() + "/" + native.Key + ": " + reason +
                " No generic fallback or unverified population conversion was applied.");
        }
        if (title || BattleState.ManualUnitReplacements.ContainsKey((mapped.unit_key, unit.IsPlayer())) || BattleProcessor.AutofixReplacements.ContainsKey(mapped.unit_key))
        { Record("protected", mapped.unit_key, "Explicit title/manual/autofix selection retained."); return mapped; }
        if (native.IsFieldNoncombatant && !BattleState.IsSiegeBattle)
        { Record("noncombatant", mapped.unit_key, "Native field participation is authoritative; no combat template was invented."); return mapped; }
        var author = NativeCandidatePool(unit.GetAttilaFaction()).FirstOrDefault(c => c.Type == native.Key && c.Key == mapped.unit_key)
            ?? NativeCandidatePool("Default").FirstOrDefault(c => c.Type == native.Key && c.Key == mapped.unit_key);
        if (!genericFallback && author != null && !ReviewedSemanticFile(author.File))
        { Record("protected", mapped.unit_key, "Edited/custom authored mapping retained."); return mapped; }
        if (NativeMappingPolicy.CohortCategory(native.Category) == null)
            return Unsupported("No audited population/weapon contract for native family " + native.Category);
        var original = mapped.unit_key == NOT_FOUND_KEY ? null : SemanticAsset(mapped.unit_key);
        string? mismatch = original == null ? "missing-active-unit" : NativeMappingPolicy.RoleMismatch(native.Category!, original, native.Key);
        if (original != null && (original.EngineKey.Length > 0 || mismatch == null && NativeMappingPolicy.RepresentationIssue(original) != null))
        {
            return Unsupported(original.EngineKey.Length > 0 ? "Combat artillery needs an explicit native-to-engine population contract." : NativeMappingPolicy.RepresentationIssue(original)!);
        }
        if (mismatch == null) { Record("retained", mapped.unit_key, "Coarse population/foot-or-mount checks passed; full weapon-role and absolute balance unverified.", author); return (mapped.unit_key, false); }
        var replacement = FindCompatibleAuthoredCandidate(unit, native, unit.GetAttilaFaction(), mapped.unit_key)
            ?? FindCompatibleAuthoredCandidate(unit, native, "Default", mapped.unit_key);
        if (replacement == null) return Unsupported(mismatch + "; no compatible authored same-family candidate.");
        string reason = mismatch + "; same native family " + native.Category + ", authored " + replacement.Type + " in " + Path.GetFileName(replacement.File);
        Record("repaired", replacement.Key, reason, replacement);
        StatFidelitySession.Note("Semantic mapping " + unit.GetAttilaFaction() + "/" + native.Key + ": " + mapped.unit_key + " -> " + replacement.Key + " (" + reason + "). Relative template scaling remains; outcome equivalence is not asserted.");
        return (replacement.Key, false);
    }

    private static void RecordSemanticReplacement(Unit unit, string finalKey)
    {
        if (!nativeMappingDecisions.TryGetValue(unit.UniqueID, out var decision)) return;
        bool explicitChoice = BattleState.ManualUnitReplacements.ContainsKey((decision.ResolvedKey, unit.IsPlayer()))
            || BattleProcessor.AutofixReplacements.ContainsKey(decision.ResolvedKey);
        if (explicitChoice || finalKey != decision.ResolvedKey)
            nativeMappingDecisions[unit.UniqueID] = decision with { Status = "protected", ResolvedKey = finalKey,
                Reason = "Explicit manual/autofix replacement of the resolved template retained: " + decision.ResolvedKey + " -> " + finalKey };
    }

    private static bool TrySemanticCohortMaximum(Unit unit, out int maximum)
    {
        maximum = 0;
        if (!NativeSemanticPolicyEnabled || unit.GetRegimentType() != RegimentType.MenAtArms || !ArmiesReader.TryGetNativeMaaDefinition(unit.GetName(), out var native)) return false;
        if (nativeMappingDecisions.TryGetValue(unit.UniqueID, out var decision) && decision.Status == "protected") return false;
        var author = NativeCandidatePool(unit.GetAttilaFaction()).FirstOrDefault(c => c.Type == native.Key)
            ?? NativeCandidatePool("Default").FirstOrDefault(c => c.Type == native.Key);
        if (author != null && (!ReviewedSemanticFile(author.File) || int.TryParse(author.Maximum, out int numeric) && numeric > 0)) return false;
        if (!nativeMappingDecisions.ContainsKey(unit.UniqueID) && SearchInTitlesFile(unit).Item1 != NOT_FOUND_KEY) return false;
        maximum = NativeMappingPolicy.CohortCategory(native.Category) switch {
            "CAVALRY" => ModOptions.GetCavalryMax(), "RANGED" => ModOptions.GetRangedMax(), "INFANTRY" => ModOptions.GetInfantryMax(), _ => 0
        };
        return maximum > 0;
    }

    public static void RefreshNativeMappings(Army army)
    {
        if (!NativeSemanticPolicyEnabled) return;
        foreach (var unit in army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms))
        {
            unit.SetIsPlayer(army.IsPlayer());
            var mapped = GetUnitKey(unit);
            unit.SetUnitKey(mapped.Item1); unit.SetIsSiege(mapped.Item2);
            unit.SetMax(GetMax(unit));
        }
    }
}
