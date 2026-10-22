using CrusaderWars.StatFidelity;
using Xunit;

public sealed class HandoffLogTests
{
    private static string Frame(string player = "10", string province = "100", string terrain = "plains") =>
        HandoffLog.BeginMarker + "\nDateYear:1066\nDateMonth:10\nBattleName:斯坦福桥之战\n" +
        "PlayerCharacter_ID:" + player + "\nPlayerCharacter_Culture:7\nLeftSide_Owner_ID:" + player + "\nLeftSide_Owner_Culture:7\n" +
        "RightSide_Owner_ID:20\nRightSide_Owner_Culture:8\n---------Left-Side-Army---------\nLeftSide_Commander_Culture:7\nLeftSide_Prowess:18\nLeftSide_Rank:4\n" +
        "---------Right-Side-Army---------\nRightSide_Commander_Culture:8\nRightSide_Prowess:20\nRightSide_Rank:4\n---------Completed---------\n" +
        "TerrainKey:" + terrain + "\nRegion:europe\nProvinceID:" + province + "\nWinterSeverity:none\nLeftSide_ID:" + player + "\nRightSide_ID:20\n" +
        "LeftSide_Name:哈罗德\nRightSide_Name:威廉\nSpecialBuilding:none\n---------Modifiers---------\n\n\n" +
        "CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|20|35|0.05\nCCSTAT|v1|COMMANDER|defender|" + player + "|29|0.05\nCCSTAT|v1|END|snapshot-1\n" +
        "BattleType:BATTLE\n" + HandoffLog.EndMarker + "\nKeyword:CRUSADERCONFLICTS\n";

    [Fact]
    public void LatestCompleteGroupOwnsAllIdsLocationAndUnicodeNames()
    {
        var selected = HandoffLog.SelectLatest(Frame("10", "100") + "unrelated console command\n" + Frame("30", "200", "forest"), true);
        Assert.Equal("30", selected.Fields["PlayerCharacter_ID"]);
        Assert.Equal("30", selected.Fields["LeftSide_ID"]);
        Assert.Equal("200", selected.Fields["ProvinceID"]);
        Assert.Equal("forest", selected.Fields["TerrainKey"]);
        Assert.Equal("哈罗德", selected.Fields["LeftSide_Name"]);
        Assert.DoesNotContain("PlayerCharacter_ID:10\n", selected.Text);
        Assert.True(StatExportParser.Parse(selected.Text).Complete);
    }

    [Fact]
    public void IncompleteLatestGroupCannotReviveThePreviousBattle()
    {
        Assert.Throws<InvalidDataException>(() => HandoffLog.SelectLatest(Frame() + HandoffLog.BeginMarker + "\nDateYear:1067\n", true));
    }

    [Fact]
    public void MissingCurrentPlayerCannotBorrowItsHistoricalValue()
    {
        string latest = Frame("30", "200").Replace("PlayerCharacter_ID:30\n", "");
        var error = Assert.Throws<InvalidDataException>(() => HandoffLog.SelectLatest(Frame() + latest, true));
        Assert.Contains("PlayerCharacter_ID", error.Message);
    }

    [Fact]
    public void MissingCurrentTerrainCannotBorrowAFormerRiverLocation()
    {
        string latest = Frame("30", "200").Replace("TerrainKey:plains\n", "");
        Assert.Throws<InvalidDataException>(() => HandoffLog.SelectLatest(Frame("10", "100", "forest") + latest, true));
    }

    [Fact]
    public void DuplicateCurrentIdentityIsAmbiguousEvenIfOneValueLooksValid()
    {
        Assert.Throws<InvalidDataException>(() => HandoffLog.SelectLatest(Frame().Replace("PlayerCharacter_ID:10", "PlayerCharacter_ID:10\nPlayerCharacter_ID:30"), true));
    }

    [Fact]
    public void UnknownLatestProtocolCannotReviveAnOlderSupportedFrame()
    {
        Assert.Throws<InvalidDataException>(() => HandoffLog.SelectLatest(Frame() + Frame().Replace("CCHANDOFF|v1|", "CCHANDOFF|v2|"), true));
    }

    [Fact]
    public void UnframedHistoryIsOnlyAcceptedForExplicitLegacyCompatibility()
    {
        Assert.Throws<InvalidDataException>(() => HandoffLog.SelectLatest("PlayerCharacter_ID:10", true));
        Assert.False(HandoffLog.SelectLatest("PlayerCharacter_ID:10", false).IsFramed);
    }
}
