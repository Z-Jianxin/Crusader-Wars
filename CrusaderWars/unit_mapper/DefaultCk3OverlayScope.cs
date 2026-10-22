using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace CrusaderWars.unit_mapper;

public enum MapperOverlayScope { Unrestricted, UnrecognizedRevision, Additive, Asian }

/// <summary>
/// Reviewed packaged compatibility overlays are additive outside their region in
/// DefaultCK3. Exact hashes preserve the precedence of edited/custom mapper files.
/// The manifest covers the four shipped DefaultCK3 eras inspected on 2026-10-22.
/// </summary>
public static class DefaultCk3OverlayScope
{
    private static readonly Dictionary<string, string[]> Fingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AsiaExpansionProject_Factions.xml"] = ["509003B50E79AE63880FA114AA3BD4C1C775B348EFB5EF905179D37A9C26C814", "4B64A192C1774233A566863E8D3730F8B95F33843A00B773D125CCFAEE043982", "C7BAF5F323AEF286F584859F6D90820A252E874F40BE12A82F71692FE0AD1ED6", "1BF2C734A57806F8859FEC93D2736F49213E4EAFAB9BEDC6E78299BD1623D081"],
        ["MoreBookmarksPlus_Factions.xml"] = ["6D5DA1AC31A4B6A1991A36F2CD14AF4672299881590FC2D1D3C42845C99F4FD5", "9F1131DEA5B8553891986BC0F0B3D44BE531E6E12A87F3774D1C8C96F5C7621F", "765B6041430AFB50B8F421E86FE5EA6D6702FEEA2ED8BAC929611B29288617F0", "294FDC621422360665CAD948C5531C83E5BE6E792602827136FFB22B22CDB5C1"],
        ["Submod_DahanChina_Factions.xml"] = ["09D87F3A8F689167868D748C73EDAC9EDEA4A147A51BABCD40A21EE908E8C100", "0B275012F49DEA3FDBE178561A5978D95D08736B7FC34060B2D6C0F2268539E6", "E54426751194F3D89D48015D01235E72D65B2706CA3760ECEA3E143F109B3F07", "D12A43A278E40A706D7548132D3FF8F1C42E309DD6CFCD92F288D616379173AB"],
        ["Submod_DahanChina_AsiaExpansionProject_Factions.xml"] = ["AB1970A998EA68ABE23C2FE1892623E5FD2237DE16CB2CDE17FF5A1B0ECA7A6D", "4D1BB72FEA4F33CA8EACDE0255B62890DC8064240CFF53BC4FAED3FF85B09C4B", "0B67B80AD2606612669616552E901FC116F08C2AD790C4CE72A8FE2735099288", "9B3AFE66ECB91253516D452221706C41AA0453F232E2728DDD29A6C4C45FEA03"],
        ["AsiaExpansionProject_Cultures.xml"] = ["AB28E582EE40FFF0425BD8DD0AB38BB6A6BE1859C40D48C951239BB07F3183DD", "C8E0E3B98A02F3349B6341451E3457EBB59231C29222D2AE24109EA17F8E40F5", "26CE65CEDE7DDD5CD8E7DCDDCFD4F6DEF26F92FF2C3A41FE5AC901E93CA661A4", "EF12B79D2B8215848C19E30FC964BA7EB7F156865656F6E8460150A7B258D56A"],
        ["MoreBookmarksPlus_Cultures.xml"] = ["6E35A4BDAA561F5DF648C05DBF401179EBEA7C9188D62B525B4AC04A724DE9C5", "AFBD387D1922ABEBD684F1A60B6B35221D8B5F66A28F816C3EF9DEAC35D4D14C", "D63158FCC8CEC24202B2CF7A89499D5015D050A71333DA6CB7F758207BEED512", "75C0E32BE505FB47D942F2763E97C724C62AEFF717DAAED0FEBEAE8E3952642C"],
        ["Submod_DahanChina_Cultures.xml"] = ["DB0701A0B8C26413CE0DF6635CC219E12F8861D4873EEEBF7EDE1F247F0B2248", "E43329FF344ADB4CDBC88483697172637B004EC69AA0F940ACA59AD387220165", "56B3EDE924BE2F54F3D760DBF6F87763FD36399CFEE1BB320DD5AAEF32802D84", "4D27076890FCB64F560643C73B4E19AD8DE46CD8E28D02F0A74ACEF20DF2DE84"],
        ["Submod_DahanChina_AsiaExpansionProject_Cultures.xml"] = ["4F12FA20778A96F55280252F92B9BCA4D8058B16A2599999CC9F86BD86C4233E", "C7C5ED73691E9D48FD2399959DC0971C383D16686B64BAB41A7E2FF89B61EF54", "3AD8A4956917C6464C3929D67F2C5E1652A77F59C7318AC109C5DBF46C24C8D0", "742D72F44F64FA37E8BFE136ADA37B4D6659FAB7CFD01241CABA6B6E660CF81F"]
    };

    // Positive regional scopes retain existing Dahan/AEP Asian overrides, including
    // the heritage_chinese/Han Empire path used by vanilla All Under Heaven.
    private static readonly HashSet<string> AsianHeritages = new(StringComparer.Ordinal) {
        "heritage_ainuic", "heritage_austronesian", "heritage_buyeo", "heritage_chinese", "heritage_han",
        "heritage_tibetan", "heritage_tai", "heritage_burman", "heritage_mongolic", "heritage_korean",
        "heritage_hmongic", "heritage_japonic", "heritage_tungusic", "heritage_mon_khmer", "heritage_qiangic", "heritage_viet"
    };
    private static readonly HashSet<string> AsianFactions = new(StringComparer.Ordinal) {
        "Han Empire", "Chinese", "Korean", "Yamato", "Tibetan", "Mongolic", "Tai", "Burman", "Vietic", "Khmer",
        "Chamic", "Malayic", "Filipino", "Irrawaddy", "Sulawesi", "Qiangic", "Loloish", "Antai"
    };

    public static MapperOverlayScope Identify(string? playthrough, string filename, string sha256)
    {
        if (playthrough != "DefaultCK3" || !Fingerprints.TryGetValue(filename, out var known)) return MapperOverlayScope.Unrestricted;
        if (!known.Contains(sha256, StringComparer.OrdinalIgnoreCase)) return MapperOverlayScope.UnrecognizedRevision;
        return filename.StartsWith("MoreBookmarksPlus_", StringComparison.OrdinalIgnoreCase) ? MapperOverlayScope.Additive : MapperOverlayScope.Asian;
    }

    /// <summary>Filters only the caller-owned in-memory document. Never writes mapper XML.</summary>
    public static int Apply(XmlDocument overlay, IReadOnlyList<XmlDocument> official, MapperOverlayScope scope)
    {
        if (scope is MapperOverlayScope.Unrestricted or MapperOverlayScope.UnrecognizedRevision || overlay.DocumentElement == null) return 0;
        string root = overlay.DocumentElement.Name;
        var authority = official.Where(d => d.DocumentElement?.Name == root)
            .SelectMany(d => d.DocumentElement!.ChildNodes.Cast<XmlNode>()).OfType<XmlElement>()
            .Where(e => e.HasAttribute("name")).GroupBy(e => e.GetAttribute("name"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        int removed = 0;
        foreach (var block in overlay.DocumentElement.ChildNodes.Cast<XmlNode>().OfType<XmlElement>())
        {
            string name = block.GetAttribute("name");
            bool regional = scope == MapperOverlayScope.Asian && (root == "Cultures" ? AsianHeritages : AsianFactions).Contains(name);
            if (regional || !authority.TryGetValue(name, out var baseBlocks)) continue;
            var baseNodes = baseBlocks.SelectMany(b => b.ChildNodes.Cast<XmlNode>()).OfType<XmlElement>().ToArray();
            var authorityBlock = root == "Cultures" ? baseBlocks[^1] : baseBlocks[0];
            foreach (XmlAttribute attribute in authorityBlock.Attributes)
                if (attribute.Name != "name") block.SetAttribute(attribute.Name, attribute.Value);
            foreach (var node in block.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().ToArray())
            {
                bool conflicts = root == "Cultures"
                    ? node.Name == "Culture" && baseNodes.Any(b => b.Name == "Culture" && b.GetAttribute("name") == node.GetAttribute("name"))
                    : node.Name == "MenAtArm"
                        ? baseNodes.Any(b => b.Name == "MenAtArm" && b.GetAttribute("type") == node.GetAttribute("type"))
                        : node.Name is "General" or "Knights" or "Levies" or "Garrison" && baseNodes.Any(b => b.Name == node.Name);
                if (conflicts) { block.RemoveChild(node); removed++; }
            }
        }
        return removed;
    }
}
