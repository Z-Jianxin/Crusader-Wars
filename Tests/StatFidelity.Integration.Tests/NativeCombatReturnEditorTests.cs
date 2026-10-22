using CrusaderWars.data.battle_results.editors;
using Xunit;

public sealed class NativeCombatReturnEditorTests
{
    [Theory]
    [InlineData("attacker")]
    [InlineData("defender")]
    public void BothWinnersPreserveWoundedBootstrapAndHistoricalState(string winner)
    {
        string original = Combat("382.85451", "72.96276", "0", "10.00000");
        var updates = new[] { new NativeCombatReturnUpdate("attacker", "73", 382.85451m, 72.96276m, 191.42725m, 456, 265),
            new NativeCombatReturnUpdate("defender", "7", 0, 10, 0, 10, 10) };
        string actual = NativeCombatReturnEditor.Edit(original, winner, updates);
        Assert.Contains("current=191.42725 soft_casualties=72.96276", actual);
        Assert.Contains("current=0 soft_casualties=10.00000", actual);
        Assert.Contains("initial_men=477 total_fighting_men=477 total_levy_men=477", actual);
        Assert.Contains("starting=477", actual); Assert.Contains("phase=pursuit days=3", actual);
        Assert.Contains("winning_side=" + winner, actual); Assert.Contains("wiped=no", actual);
        Assert.Contains("note=\"quoted } { regiment=999 current=999\"", actual);
        // Preparation is repeatable against the exact original input, not cumulative.
        Assert.Equal(actual, NativeCombatReturnEditor.Edit(original, winner, updates));
    }

    [Theory]
    [InlineData("0", "10.00000", 0, 10, 10)]
    [InlineData("0", "0", 0, 0, 0)]
    [InlineData("0.99230", "0.00597", 0.99230, 0.00597, 1)]
    [InlineData("1.00000", "0.00000", 1, 0, 1)]
    public void NoLossRetainsActiveAndSoftWithoutRoundingThemIndependently(string activeText, string softText,
        double activeValue, double softValue, int stock)
    {
        decimal active = (decimal)activeValue, soft = (decimal)softValue;
        string original = Combat(activeText, softText, "0", "0");
        string actual = NativeCombatReturnEditor.Edit(original, "attacker", new[] {
            new NativeCombatReturnUpdate("attacker", "73", active, soft, active, stock, stock),
            new NativeCombatReturnUpdate("defender", "7", 0, 0, 0, 0, 0) });
        Assert.Contains("soft_casualties=" + softText, actual);
        Assert.Contains("starting=477", actual);
    }

    [Fact]
    public void ReservedEquipmentRemainsSeparateFromWoundedAndActive()
    {
        string actual = NativeCombatReturnEditor.Edit(Combat("5", "0", "0", "0"), "defender", new[] {
            new NativeCombatReturnUpdate("attacker", "73", 5, 0, 2, 20, 17, EquipmentReserve: 15),
            new NativeCombatReturnUpdate("defender", "7", 0, 0, 0, 10, 10, EquipmentReserve: 10) });
        Assert.Contains("regiment=73 starting=477 current=2 soft_casualties=0", actual);
        Assert.Contains("regiment=7 starting=10 current=0 soft_casualties=0", actual);
    }

    [Fact]
    public void OnlyExplicitUnavailableCharacterCanClearItsOwnFractionalPool()
    {
        var update = new NativeCombatReturnUpdate("attacker", "73", .99230m, .00597m, 0, 1, 0, CharacterUnavailable: true, IsCharacter: true);
        string original = Combat("0.99230", "0.00597", "0", "0");
        var zero = new NativeCombatReturnUpdate("defender", "7", 0, 0, 0, 0, 0);
        string actual = NativeCombatReturnEditor.Edit(original, "attacker", new[] { update, zero });
        Assert.Contains("current=0 soft_casualties=0", actual);
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(original, "attacker", new[] { update with { CharacterUnavailable=false }, zero }));
    }

    [Theory]
    [InlineData("0.000001", "0", 1)]
    [InlineData("1", "0.000001", 2)]
    [InlineData("1", "0", 2)]
    [InlineData("1", "0", 0)]
    public void UnsupportedPrecisionAndNonconservingStocksReject(string activeText, string softText, int stock)
    {
        decimal active=decimal.Parse(activeText, System.Globalization.CultureInfo.InvariantCulture),
            soft=decimal.Parse(softText, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(Combat(activeText,softText,"0","0"), "attacker", new[] {
            new NativeCombatReturnUpdate("attacker","73",active,soft,active,stock,stock),
            new NativeCombatReturnUpdate("defender","7",0,0,0,0,0) }));
    }

    [Fact]
    public void ChangedMissingDuplicateOrUnknownRecordsRejectBeforeAnyResult()
    {
        string original=Combat("8", "2", "0", "0");
        var a=new NativeCombatReturnUpdate("attacker","73",8,2,4,10,6);
        var d=new NativeCombatReturnUpdate("defender","7",0,0,0,0,0);
        foreach (string bad in new[] { original.Replace("current=8", "current=7"),
            original.Replace("soft_casualties=2", "soft_casualties=1"),
            original.Replace("soft_casualties=2", ""),
            original.Replace("regiment=73", "regiment=73 regiment=74"),
            original.Replace("current=8", "current=8 current=8"), original[..^1] })
            Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(bad,"attacker",new[] {a,d}));
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(original,"attacker",new[] {a}));
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(original,"attacker",new[] {a,d,d}));
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(original,"attacker",new[] {a,d with { RegimentId="70" }}));
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(original,"attacker",new[] {a with { ActiveRemaining=9,ReturnedStock=11 },d}));
    }

    [Theory]
    [InlineData(10, 10, 10, 0, 0)] // All-soft/no-loss pool is alive, not killed.
    [InlineData(12, 10, 10, 2, 0)] // Existing hard losses remain historical.
    [InlineData(10, 6, 4, 4, 2)] // Joint A+S rounding in main and pursuit.
    public void ResultLossAccountingIncludesSoftStockAndLeavesCompletionAccumulatorEmpty(
        int initial, int mainStock, int finalStock, int expectedMain, int expectedPursuit)
    {
        string group = "{ knight=4294967295 initial_count=" + initial +
            " final_count=0 main_kills=0 pursuit_kills=0 main_losses=0 pursuit_losses_maa=0 pursuit_losses_self=0 }";
        string original = "combat_results={ 0={ war_score=0 end_date=1066.1.1 attacker={ surviving_soldiers=0 regiment_stats={ " + group +
            " } } defender={ surviving_soldiers=0 regiment_stats={ " + group + " } } } }";
        var a = new CombatResultStatisticsUpdate("attacker", null, null, 7, 3, finalStock, mainStock-finalStock);
        var d = a with { Side="defender" };
        string actual = CombatResultStatisticsEditor.Edit(original, "0", new[] {a,d});
        Assert.Contains("main_losses="+expectedMain, actual);
        Assert.Contains("pursuit_losses_self="+expectedPursuit, actual);
        Assert.Contains("surviving_soldiers="+finalStock, actual);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(actual, "final_count=0").Count);
        Assert.Contains("war_score=0 end_date=1066.1.1", actual);
        Assert.Equal(actual, CombatResultStatisticsEditor.Edit(original,"0",new[] {a,d}));
    }

    [Fact]
    public void ExactCombatIdentityAndExistingLifecycleFieldsDoNotDuplicate()
    {
        string original=Combat("8","2","0","0").Replace("phase=main", "winning_side=defender wiped=yes phase=main");
        var updates=new[] { new NativeCombatReturnUpdate("attacker","73",8,2,4,10,6),
            new NativeCombatReturnUpdate("defender","7",0,0,0,0,0) };
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.Edit(original,"attacker",updates,"1"));
        string actual=NativeCombatReturnEditor.Edit(original,"attacker",updates,"0");
        Assert.Equal(1,System.Text.RegularExpressions.Regex.Matches(actual,"winning_side=").Count);
        Assert.Equal(1,System.Text.RegularExpressions.Regex.Matches(actual,"wiped=").Count);
        Assert.Contains("winning_side=attacker wiped=no",actual);
    }

    [Fact]
    public void ExactSectionReplacementCannotEditLongerIdOrAnotherCombat()
    {
        string original=Combat("8","2","0","0");
        var updates=new[] { new NativeCombatReturnUpdate("attacker","73",8,2,4,10,6),
            new NativeCombatReturnUpdate("defender","7",0,0,0,0,0) };
        string edited=NativeCombatReturnEditor.Edit(original,"attacker",updates,"0");
        string other="10="+original[2..];
        string section="combats={ "+other+" # preserve other record\n "+original+" 99=none }";
        string result=NativeCombatReturnEditor.ReplaceInSection(section,"0",original,edited);
        Assert.Equal("combats={ "+other+" # preserve other record\n "+edited+" 99=none }",result);
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.ReplaceInSection("combats={ "+other+" }","0",original,edited));
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.ReplaceInSection("combats={ "+original+original+" }","0",original,edited));
        Assert.Throws<InvalidDataException>(() => NativeCombatReturnEditor.ReplaceInSection(section.Replace("current=8","current=7"),"0",original,edited));
    }

    [Fact]
    public void ExplicitInactiveCharacterIdentityDoesNotBecomeAnOrdinaryWoundedPool()
    {
        string original=Combat("0","0.00000","0","0");
        var inactive=new NativeCombatReturnUpdate("attacker","73",0,0,0,1,1,IsCharacter:true);
        var zero=new NativeCombatReturnUpdate("defender","7",0,0,0,0,0);
        string actual=NativeCombatReturnEditor.Edit(original,"attacker",new[] {inactive,zero});
        Assert.Contains("regiment=73 starting=477 current=0 soft_casualties=0.00000",actual);
        Assert.Throws<InvalidDataException>(()=>NativeCombatReturnEditor.Edit(original,"attacker",new[] {inactive with {IsCharacter=false},zero}));
        Assert.Throws<InvalidDataException>(()=>NativeCombatReturnEditor.Edit(original,"attacker",new[] {inactive with {CharacterUnavailable=true,ReturnedStock=0},zero}));
    }

    [Fact]
    public void SeparatelyEvidencedCommanderRemovalDoesNotInventAnInactivePersonalFight()
    {
        string original=Combat("0","0","0","0");
        var removed=new NativeCombatReturnUpdate("attacker","73",0,0,0,1,0,CharacterUnavailable:true,
            IsCharacter:true,IndependentCommanderRemoval:true);
        var zero=new NativeCombatReturnUpdate("defender","7",0,0,0,0,0);
        Assert.Contains("current=0 soft_casualties=0",NativeCombatReturnEditor.Edit(original,"defender",new[] {removed,zero}));
        Assert.Throws<InvalidDataException>(()=>NativeCombatReturnEditor.Edit(original,"defender",new[] {removed with {IndependentCommanderRemoval=false},zero}));
        Assert.Throws<InvalidDataException>(()=>NativeCombatReturnEditor.Edit(original,"defender",new[] {removed with {CharacterUnavailable=false,ReturnedStock=1},zero}));
    }

    private static string Combat(string activeA, string softA, string activeD, string softD) =>
        "0={ phase=main days=2 base_combat_width=90 note=\"quoted } { regiment=999 current=999\" " +
        "attacker={ armies={1} initial_men=477 total_fighting_men=477 total_levy_men=477 " +
        "levies={ { regiment=73 starting=477 current="+activeA+" soft_casualties="+softA+" } } } " +
        "defender={ armies={2} initial_men=10 total_fighting_men=10 total_levy_men=10 " +
        "men_at_arms={ { regiment=7 starting=10 current="+activeD+" soft_casualties="+softD+" } } } }";
}
