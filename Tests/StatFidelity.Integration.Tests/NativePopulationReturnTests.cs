using System.Globalization;
using System.Reflection;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class NativePopulationReturnTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-active-return-"+Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string,string> options;
    private readonly string? previousMode;
    private readonly Culture culture = new("7", "native", "native");
    public NativePopulationReturnTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch,"data","save_file_data","temp")); Environment.CurrentDirectory=scratch;
        options=(Dictionary<string,string>)typeof(ModOptions).GetField("optionsValuesCollection",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("StatFidelityMode",out previousMode);options["StatFidelityMode"]="Enabled";
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"),"42","0");
        BattleResult.CharacterFates.Clear(); BattleResult.CombatID="0";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriterKeepsSoftSourcesAndInactiveKnightAndIsRepeatableForEitherWinner(bool attackerWins)
    {
        var (a,d,unit,script)=Fixture();
        BattleResult.IsAttackerVictorious=attackerWins;
        BattleResult.SynchronizeNativeKnightRegiments(new[] {a,d});
        Assert.Empty(BattleResult.CharacterFates.Entries);
        BattleResult.EditCombatFile(new() {a},new() {d});
        string once=File.ReadAllText(Writter.DataTEMPFilesPaths.Combats_Path());
        Assert.Contains("regiment=73 starting=12 current=2 soft_casualties=2.00000",once);
        Assert.Contains("regiment=7 starting=1 current=0 soft_casualties=1.00000",once);
        Assert.Contains("regiment=8 starting=10 current=0 soft_casualties=10",once);
        Assert.Contains("total_fighting_men=12 total_levy_men=12",once);
        Assert.Contains("winning_side="+(attackerWins?"attacker":"defender"),once);
        Assert.Equal(new[] {"2","2"},a.ArmyRegiments[0].Regiments.Select(r=>r.CurrentNum));
        Assert.Equal(4,a.ArmyRegiments[0].CurrentNum);
        Assert.Equal(1,d.ArmyRegiments.Single(r=>r.Type==RegimentType.Knight).CurrentNum);
        BattleResult.EditCombatFile(new() {a},new() {d});
        Assert.Equal(once,File.ReadAllText(Writter.DataTEMPFilesPaths.Combats_Path()));
    }

    [Fact]
    public void SourceDistributionMismatchDoesNotPublishTemporaryCombatOrMutateOriginal()
    {
        var (a,d,_,_)=Fixture();
        string before=File.ReadAllText(Writter.DataFilesPaths.Combats_Path());
        a.ArmyRegiments[0].Regiments[0].SetSoldiers("3");
        a.ArmyRegiments[0].Regiments[1].SetSoldiers("1"); // Same total, wrong exact source result.
        Assert.Throws<InvalidDataException>(()=>BattleResult.EditCombatFile(new() {a},new() {d}));
        Assert.False(File.Exists(Writter.DataTEMPFilesPaths.Combats_Path()));
        Assert.Equal(before,File.ReadAllText(Writter.DataFilesPaths.Combats_Path()));
        Assert.Equal(BattleResult.Original_Player_Combat,BattleResult.Player_Combat);
    }

    [Fact]
    public void StatisticsUseJointPhaseStocksOnceAndDoNotReportOldSoftAsDead()
    {
        var (a,d,unit,script)=Fixture();
        var roster=new[] {new DeployedFormation(script,8)};
        var reports=BattleReportEvidence.Validate(roster,new[] {script+"-4",script+"-2"},
            new[] {"kills_"+script+"-3","kills_"+script+"-5"});
        var statistics=CombatResultStatistics.Build(new[] {a},new[] {d},roster,reports);
        var attacker=Assert.Single(statistics.Updates.Where(r=>r.Side=="attacker"));
        Assert.Equal(4,attacker.FinalCount);Assert.Equal(2,attacker.PursuitLosses);
        Assert.Equal(3,attacker.MainKills);Assert.Equal(2,attacker.PursuitKills);
        var allSoft=Assert.Single(statistics.Updates.Where(r=>r.Side=="defender"&&r.KnightId==null));
        Assert.Equal(10,allSoft.FinalCount);Assert.Equal(0,allSoft.PursuitLosses);
        var inactive=Assert.Single(statistics.Updates.Where(r=>r.KnightId=="42"));
        Assert.Equal(1,inactive.FinalCount);Assert.Equal(0,inactive.MainKills);
        Assert.Empty(BattleResult.CharacterFates.Entries);
    }

    [Fact]
    public void InactiveActorWithNoSoftPoolRetainsIdentityWithoutCreatingAWoundOrFate()
    {
        var (a,d,_,_)=Fixture(inactiveKnightSoft: 0);
        BattleResult.SynchronizeNativeKnightRegiments(new[] {a,d});
        BattleResult.EditCombatFile(new() {a},new() {d});
        string result=File.ReadAllText(Writter.DataTEMPFilesPaths.Combats_Path());
        Assert.Contains("regiment=7 starting=1 current=0 soft_casualties=0.00000",result);
        Assert.Equal(1,d.ArmyRegiments.Single(r=>r.ID=="7").CurrentNum);
        Assert.Empty(BattleResult.CharacterFates.Entries);
    }

    private (Army,Army,Unit,string) Fixture(decimal inactiveKnightSoft = 1)
    {
        var a=new Army("1","attacker",true);var d=new Army("2","defender",false);
        Regiment Source(string id,int count) {var s=new Regiment(id,"0");s.SetSoldiers(count.ToString());s.SetMax(count.ToString());s.SetCulture("7");return s;}
        var ar=new ArmyRegiment("73") {CurrentNum=10,StartingNum=12};ar.SetType(RegimentType.Levy);ar.SetRegiments(new() {Source("100",5),Source("101",5)});a.SetArmyRegiments(new() {ar});
        var dr=new ArmyRegiment("8") {CurrentNum=10,StartingNum=10};dr.SetType(RegimentType.Levy);dr.SetRegiments(new() {Source("200",10)});
        var knight=new ArmyRegiment("7") {CurrentNum=1,StartingNum=1};knight.SetType(RegimentType.Knight,"42");d.SetArmyRegiments(new() {dr,knight});
        StatFidelitySession.RegisterNativePopulationBudget(NativePopulationLedger.Create("attacker","1","73",RegimentType.Levy,"Levy",10,8,2,
            new[] {new NativePopulationSourceStock("100","0",5,"7"),new NativePopulationSourceStock("101","0",5,"7")}));
        StatFidelitySession.RegisterNativePopulationBudget(NativePopulationLedger.Create("defender","2","8",RegimentType.Levy,"Levy",10,0,10,
            new[] {new NativePopulationSourceStock("200","0",10,"7")}));
        StatFidelitySession.RegisterNativePopulationBudget(NativePopulationLedger.Create("defender","2","7",RegimentType.Knight,"42",1,0,inactiveKnightSoft,
            Array.Empty<NativePopulationSourceStock>()));
        var unit=new Unit("Levy",8,culture,RegimentType.Levy);a.SetUnits(new() {unit});
        string script="0_attacker_army1_TYPELevy_"+unit.UniqueID+"_CULTURE_0";
        a.CasualitiesReports.Add(new UnitCasualitiesReport(RegimentType.Levy,"Levy",culture,8,4,2,0,"native",script));
        StatFidelitySession.FinalizeNativePopulationOutcomes(new[] {a,d});
        Assert.True(StatFidelitySession.TryGetNativePopulationOutcome("attacker","73",out var result));
        Assert.Equal(4,result.MainActiveRemaining);Assert.Equal(2,result.FinalActiveRemaining);
        Assert.Equal(6,result.MainReturnedStock);Assert.Equal(4,result.ReturnedStock);
        foreach(var s in ar.Regiments)s.SetSoldiers(result.SourceOutcomes.Single(o=>o.Budget.RegimentId==s.ID).ReturnedStock.ToString(CultureInfo.InvariantCulture));
        ar.CurrentNum=result.ReturnedStock;
        string combat="0={ phase=main days=2 base_combat_width=50 "+
            "attacker={ armies={1} initial_men=12 total_fighting_men=12 total_levy_men=12 levies={ {regiment=73 starting=12 current=8 soft_casualties=2.00000} } } "+
            "defender={ armies={2} initial_men=11 total_fighting_men=11 total_levy_men=10 levies={ {regiment=8 starting=10 current=0 soft_casualties=10} } "+
            "men_at_arms={ {regiment=7 starting=1 current=0 soft_casualties="+inactiveKnightSoft.ToString("0.00000",CultureInfo.InvariantCulture)+"} } } }";
        BattleResult.Original_Player_Combat=combat;BattleResult.Player_Combat=combat;
        File.WriteAllText(Writter.DataFilesPaths.Combats_Path(),"combats={ "+combat+" 10=none }");
        return (a,d,unit,script);
    }
    public void Dispose()
    {
        BattleResult.CharacterFates.Clear();options["StatFidelityMode"]="Disabled";StatFidelitySession.Begin("");
        if(previousMode==null)options.Remove("StatFidelityMode");else options["StatFidelityMode"]=previousMode;
        Environment.CurrentDirectory=previous;
        string path=Path.GetFullPath(scratch);
        if(Path.GetDirectoryName(path)==Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))Directory.Delete(path,true);
    }
}
