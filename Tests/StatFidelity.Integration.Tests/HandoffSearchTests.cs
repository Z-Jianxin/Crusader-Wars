using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.StatFidelity;
using System.Reflection;
using Xunit;

public sealed class HandoffSearchTests : IDisposable
{
    private readonly string previousDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-handoff-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options;
    private readonly Dictionary<string, string> previousOptions;

    public HandoffSearchTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data"));
        Environment.CurrentDirectory = scratch;
        options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        previousOptions = new Dictionary<string, string>(options);
        options["StatFidelityMode"] = "Enabled";
        options["BattleScale"] = "100%";
    }

    [Fact]
    public void ProductionSearchUsesOnlyLatestFramedIdentityLocationAndChineseNames()
    {
        DataSearch.Search(Frame("10", "100", "plains", "1066"));
        TerrainGenerator.isRiverBattle(true);
        TerrainGenerator.isStraitBattle(true);
        TerrainGenerator.isCoastalBattle(true);
        TerrainGenerator.isUniqueBattle(true);
        DataSearch.Search((Frame("10", "100", "plains", "1066") + Frame("30", "200", "forest", "1067")).Replace("\n", "\r\n"));
        Assert.Equal("30", DataSearch.Player_Character!.GetID());
        Assert.Equal("30", CK3LogData.LeftSide.GetCommander().id);
        Assert.Equal("30", CK3LogData.LeftSide.GetMainParticipant().id);
        Assert.Equal("哈罗德", CK3LogData.LeftSide.GetCommander().name);
        Assert.Equal("英格兰王国", CK3LogData.LeftSide.GetRealmName());
        Assert.Equal("诺曼底公国", CK3LogData.RightSide.GetRealmName());
        Assert.Equal("200", BattleResult.ProvinceID);
        Assert.Equal("forest", TerrainGenerator.TerrainType);
        Assert.Equal("europe", TerrainGenerator.Region.Trim());
        Assert.False(TerrainGenerator.isRiver || TerrainGenerator.isStrait || TerrainGenerator.isCoastal || TerrainGenerator.isUnique);
        Assert.Equal(1067, Date.Year);
        Assert.True(StatFidelitySession.Export.Complete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrIncompleteCurrentFrameInvalidatesPreviouslyAcceptedExport(bool missingPlayer)
    {
        DataSearch.Search(Frame("10", "100", "plains", "1066"));
        string latest = missingPlayer
            ? Frame("30", "200", "forest", "1067").Replace("PlayerCharacter_ID:30\n", "")
            : HandoffLog.BeginMarker + "\nDateYear:1067\n";
        Assert.Throws<InvalidOperationException>(() => DataSearch.Search(Frame("10", "100", "plains", "1066") + latest));
        Assert.False(StatFidelitySession.Export.Complete);
        Assert.Empty(StatFidelitySession.PendingProfiles);
        Assert.False(StatFidelitySession.SaveEvidenceValidated);
    }

    [Fact]
    public void MissingNativeCommanderDoesNotBecomeTheCurrentArmyOwner()
    {
        string frame = Frame("10", "100", "plains", "1066")
            .Replace("LeftSide_ID:10\n", "LeftSide_ID:4294967295\n")
            .Replace("COMMANDER|defender|10|29", "COMMANDER|defender|none|0");
        DataSearch.Search(frame);
        Assert.Equal("none", CK3LogData.LeftSide.GetCommander().id);
        Assert.Equal("10", CK3LogData.LeftSide.GetMainParticipant().id);
    }

    [Fact]
    public void GeneratedUnitCardNameRoundTripsUnicodeQuotesTabsNewlinesAndDollarLiterally()
    {
        string displayName = "Commander 诺曼底\r\n\"威廉\"\t$1";
        Type writer = typeof(DataSearch).Assembly.GetType("CrusaderWars.locs.UnitsCardsNames")!;
        string row = (string)writer.GetMethod("ReplaceUnitNameTsv", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { "land_units_onscreen_name_test\tOld\ttrue", displayName })!;
        string path = Path.Combine(scratch, "unit-name.loc.tsv");
        File.WriteAllText(path, "key\ttext\ttooltip\n#Loc;1;text/db/unit-name.loc\t\t\n" + row + "\n");
        var parsed = TsvTable.Read(path);
        Assert.Equal(displayName, Assert.Single(parsed.Rows)["text"]);
        Assert.Equal("true", parsed.Rows[0]["tooltip"]);
    }

    private static string Frame(string player, string province, string terrain, string year) =>
        HandoffLog.BeginMarker + "\nDateYear:" + year + "\nDateMonth:10\nBattleName:斯坦福桥之战\n" +
        "TOOLTIP:LANDED_TITLE,202 L; 英格兰王国!!!!\nTOOLTIP:LANDED_TITLE7219 L; 诺曼底公国!!!!\n" +
        "PlayerCharacter_ID:" + player + "\nPlayerCharacter_Culture:7\nLeftSide_Owner_ID:" + player + "\nLeftSide_Owner_Culture:7\n" +
        "RightSide_Owner_ID:20\nRightSide_Owner_Culture:8\n---------Left-Side-Army---------\nLeftSide_Commander_Culture:7\nLeftSide_Prowess:18\nLeftSide_Rank:4\n" +
        "---------Right-Side-Army---------\nRightSide_Commander_Culture:8\nRightSide_Prowess:20\nRightSide_Rank:4\n---------Completed---------\n" +
        "TerrainKey:" + terrain + "\nRegion:europe\nProvinceID:" + province + "\nWinterSeverity:none\nLeftSide_ID:" + player + "\nRightSide_ID:20\n" +
        "LeftSide_Name:哈罗德\nRightSide_Name:威廉\nSpecialBuilding:none\n---------Modifiers---------\n\n\n" +
        "CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|20|35|0.05\nCCSTAT|v1|COMMANDER|defender|" + player + "|29|0.05\nCCSTAT|v1|END|snapshot-1\n" +
        "BattleType:BATTLE\n" + HandoffLog.EndMarker + "\nKeyword:CRUSADERCONFLICTS\n";

    public void Dispose()
    {
        options.Clear();
        foreach (var pair in previousOptions) options[pair.Key] = pair.Value;
        Environment.CurrentDirectory = previousDirectory;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
