using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class NativePopulationLedgerTests
{
    [Fact]
    public void NativeMercenaryNoLossKeepsExactActiveSoftAndEveryOriginalChunk()
    {
        var b=Ordinary(382.85451m,72.96276m,200,200,56);
        Assert.Equal(382.85451m,b.Sources.Sum(s=>s.Active));Assert.Equal(72.96276m,b.Sources.Sum(s=>s.Soft));
        var o=NativePopulationLedger.Project(b,Rows(b,10,10,10));
        Assert.Equal(b.InitialActive,o.FinalActiveRemaining);Assert.Equal(b.PreservedSoft,o.FinalSoft);
        Assert.Equal(456,o.MainReturnedStock);Assert.Equal(456,o.ReturnedStock);
        Assert.Equal(new[] {200,200,56},o.SourceOutcomes.Select(s=>s.ReturnedStock));
        Assert.Equal(0,o.FinalPrecisionDelta);
    }

    [Fact]
    public void ThreeRepeatingDecimalSourceSharesUseExactAggregateBeforeFiveDigitFloor()
    {
        var b=Ordinary(1,2,1,1,1);
        var o=NativePopulationLedger.Project(b,Rows(b,2,2,1));
        Assert.Equal(1,o.MainActiveRemaining);Assert.Equal(.5m,o.FinalActiveRemaining);
        Assert.Equal(2,o.FinalSoft);Assert.Equal(3,o.ReturnedStock);Assert.Equal(0,o.FinalPrecisionDelta);
        Assert.Equal(new[] {1,1,1},o.SourceOutcomes.Select(s=>s.ReturnedStock));
    }

    [Fact]
    public void ZeroSoftFullStockShortcutPreservesPerSourceIdentityWithoutDecimalResidue()
    {
        var b=Ordinary(2001,0,997,1001,3);
        Assert.Equal(new decimal[] {997,1001,3},b.Sources.Select(s=>s.Active));
        Assert.All(b.Sources,s=>Assert.Equal(0,s.Soft));
        var o=NativePopulationLedger.Project(b,Rows(b,3,3,3));
        Assert.Equal(2001,o.FinalActiveRemaining);Assert.Equal(2001,o.ReturnedStock);
        Assert.Equal(new[] {997,1001,3},o.SourceOutcomes.Select(s=>s.ReturnedStock));
    }

    [Fact]
    public void NativeFiveDigitFloorAndStockUseTheSameNormalizedValue()
    {
        var b=Ordinary(2,0,2);
        var o=NativePopulationLedger.Project(b,Rows(b,20000000,20000000,10000001));
        Assert.Equal(2,o.MainActiveRemaining);Assert.Equal(1m,o.FinalActiveRemaining);
        Assert.Equal(.0000001m,o.FinalPrecisionDelta);Assert.Equal(1,o.ReturnedStock);
        Assert.Equal(1,NativePopulationLedger.NormalizeActive(1.0000001m));
    }

    [Fact]
    public void WeightedRawGuardComponentsAvoidPrematureDecimalRatioTruncation()
    {
        var b=Ordinary(3,0,3);var source=Assert.Single(b.Sources);
        var row=new NativePopulationSourceSurvival(source.Identity,1,.3333333333333333333333333333m,.3333333333333333333333333333m,
            new[] {new NativePopulationBodySurvival("guard",3,1,1,1)});
        var o=NativePopulationLedger.Project(b,new[] {row});
        Assert.Equal(1,o.FinalActiveRemaining);Assert.Equal(1,o.ReturnedStock);Assert.Equal(0,o.FinalPrecisionDelta);
    }

    [Fact]
    public void PursuitRemainderReassignmentCannotResurrectAnotherSource()
    {
        var b=Ordinary(1.8m,.2m,1,1);
        var rows=new[] {new NativePopulationSourceSurvival(b.Sources[0].Identity,9,3,3),
            new NativePopulationSourceSurvival(b.Sources[1].Identity,9,4,0)};
        var o=NativePopulationLedger.Project(b,rows);
        Assert.Equal(new[] {0,1},o.SourceOutcomes.Select(s=>s.MainReturnedStock));
        Assert.Equal(new[] {0,1},o.SourceOutcomes.Select(s=>s.ReturnedStock));
        Assert.Equal(.7m,o.MainActiveRemaining);Assert.Equal(.3m,o.FinalActiveRemaining);
        Assert.Equal(1,o.MainReturnedStock);Assert.Equal(1,o.ReturnedStock);
    }

    [Theory]
    [InlineData(10,4,6)]
    [InlineData(0,0,0)]
    public void AllSoftAndZeroPopulationsNeedNoPhysicalSurvivorEvidence(int soft,int first,int second)
    {
        var b=Ordinary(0,soft,first,second);
        var o=NativePopulationLedger.Project(b,Array.Empty<NativePopulationSourceSurvival>());
        Assert.Equal(0,o.FinalActiveRemaining);Assert.Equal(soft,o.FinalSoft);Assert.Equal(soft,o.ReturnedStock);
        Assert.Equal(new[] {first,second},o.SourceOutcomes.Select(s=>s.ReturnedStock));
        Assert.All(o.SourceOutcomes,s=>Assert.Null(s.RawSurvival));
        Assert.Throws<InvalidDataException>(()=>NativePopulationLedger.Project(b,new[] {
            new NativePopulationSourceSurvival(b.Sources[0].Identity,1,1,1)}));
    }

    [Fact]
    public void SeededPopulationCasesConserveNoLossAndRemainBoundedThroughBothPhases()
    {
        var random=new Random(563104);
        for(int sample=0;sample<250;sample++)
        {
            int[] stocks=Enumerable.Range(0,random.Next(2,9)).Select(_=>random.Next(0,1001)).ToArray();
            if(stocks.Sum()==0)stocks[0]=1;
            int n=stocks.Sum();int totalTicks=n*100000-random.Next(0,100000);
            int activeTicks=sample%13==0?0:random.Next(0,totalTicks+1);
            decimal active=activeTicks/100000m,soft=(totalTicks-activeTicks)/100000m;
            var b=Ordinary(active,soft,stocks);
            Assert.Equal(active,b.Sources.Sum(s=>s.Active));Assert.Equal(soft,b.Sources.Sum(s=>s.Soft));
            var unchanged=NativePopulationLedger.Project(b,Rows(b,10,10,10));
            Assert.Equal(active,unchanged.FinalActiveRemaining);Assert.Equal(soft,unchanged.FinalSoft);Assert.Equal(n,unchanged.ReturnedStock);
            Assert.Equal(stocks,unchanged.SourceOutcomes.Select(s=>s.ReturnedStock));
            var raw=b.Sources.Where(s=>s.Active>0).Select(s=> {
                int bodies=random.Next(1,101),main=random.Next(0,bodies+1),final=random.Next(0,main+1);
                return new NativePopulationSourceSurvival(s.Identity,bodies,main,final);
            }).ToArray();
            var o=NativePopulationLedger.Project(b,raw);
            Assert.InRange(o.FinalActiveRemaining,0,o.MainActiveRemaining);Assert.InRange(o.MainActiveRemaining,0,active);
            Assert.Equal(soft,o.FinalSoft);Assert.Equal(decimal.Ceiling(o.FinalActiveRemaining+soft),o.ReturnedStock);
            Assert.Equal(decimal.Ceiling(o.MainActiveRemaining+soft),o.MainReturnedStock);
            Assert.Equal(o.ReturnedStock,o.SourceOutcomes.Sum(s=>s.ReturnedStock));
            Assert.Equal(o.MainReturnedStock,o.SourceOutcomes.Sum(s=>s.MainReturnedStock));
            foreach(var s in o.SourceOutcomes) {Assert.InRange(s.ReturnedStock,0,s.MainReturnedStock);Assert.InRange(s.MainReturnedStock,0,s.Budget.Stock);}
            var repeated=NativePopulationLedger.Project(b,raw);
            Assert.Equal(o.FinalActiveRemaining,repeated.FinalActiveRemaining);Assert.Equal(o.SourceOutcomes,repeated.SourceOutcomes);
        }
    }

    [Theory]
    [InlineData(1,0)]
    [InlineData(.9923,.00597)]
    public void AvailableKnightRetinueAnnihilationDoesNotErasePersonalIdentity(double active,double soft)
    {
        var b=Knight((decimal)active,(decimal)soft);var raw=new NativePopulationSourceSurvival(b.Identity,30,0,0);
        var o=NativePopulationLedger.ProjectKnight(b,raw,false);
        Assert.Equal(1,o.ReturnedStock);Assert.Equal(b.InitialActive,o.FinalActiveRemaining);Assert.Equal(b.PreservedSoft,o.FinalSoft);
        Assert.False(o.CharacterUnavailable);Assert.Empty(o.SourceOutcomes);
        Assert.Equal(1,o.MainReturnedStock);
    }

    [Fact]
    public void ResolvedCharacterRemovalClearsOnlyThatIdentityAndRetainsOriginalEvidence()
    {
        var b=Knight(.9923m,.00597m);var raw=new NativePopulationSourceSurvival(b.Identity,30,0,0);
        var o=NativePopulationLedger.ProjectKnight(b,raw,true);
        Assert.True(o.CharacterUnavailable);Assert.Equal(0,o.ReturnedStock);Assert.Equal(0,o.FinalActiveRemaining);Assert.Equal(0,o.FinalSoft);
        Assert.Equal(.9923m,o.Budget.InitialActive);Assert.Equal(.00597m,o.Budget.PreservedSoft);
        Assert.Same(raw,o.CharacterSurvival);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InactivePersonalActorHasNoNewBattleFateWithoutIndependentCommanderRole(int soft)
    {
        var b=Knight(0,soft);var o=NativePopulationLedger.ProjectKnight(b,null,false);
        Assert.Equal(1,o.ReturnedStock);Assert.Equal(0,o.FinalActiveRemaining);Assert.Equal(soft,o.FinalSoft);
        Assert.Equal(soft==0,b.IsCharacterIdentityReserve);
        Assert.Throws<InvalidDataException>(()=>NativePopulationLedger.ProjectKnight(b,null,true));
        var raw=new NativePopulationSourceSurvival(b.Identity,30,0,0);
        Assert.Throws<InvalidDataException>(()=>NativePopulationLedger.ProjectKnight(b,raw,true));
        var commander=NativePopulationLedger.ProjectKnight(b,raw,true,independentCommanderRemoval:true);
        Assert.True(commander.IndependentCommanderRemoval);Assert.Equal(0,commander.ReturnedStock);Assert.Equal(0,commander.FinalSoft);
        Assert.Equal(soft,b.PreservedSoft);Assert.Equal(1,b.Stock);
    }

    [Fact]
    public void OrdinaryMissingOrInvalidPopulationCannotBorrowCharacterIdentityException()
    {
        Assert.Throws<InvalidDataException>(()=>Ordinary(0,0,1));
        Assert.Throws<InvalidDataException>(()=>Ordinary(.000001m,.999999m,1));
        Assert.Throws<InvalidDataException>(()=>Ordinary(.9m,.2m,1));
        var b=Ordinary(.5m,.5m,1);
        Assert.Throws<InvalidDataException>(()=>NativePopulationLedger.Project(b,Array.Empty<NativePopulationSourceSurvival>()));
        Assert.Throws<InvalidDataException>(()=>NativePopulationLedger.Project(b,new[] {new NativePopulationSourceSurvival("foreign",1,1,1)}));
        Assert.Throws<InvalidDataException>(()=>NativePopulationLedger.Project(b,Rows(b,1,1,1),characterUnavailable:true));
    }

    private static NativePopulationBudget Ordinary(decimal active,decimal soft,params int[] stocks)=>
        NativePopulationLedger.Create("attacker","1","10",RegimentType.Levy,"Levy",stocks.Sum(),active,soft,
            stocks.Select((n,i)=>new NativePopulationSourceStock((100+i).ToString(),"0",n,"7")).ToArray());
    private static NativePopulationBudget Knight(decimal active,decimal soft)=>
        NativePopulationLedger.Create("defender","2","11",RegimentType.Knight,"42",1,active,soft,Array.Empty<NativePopulationSourceStock>());
    private static NativePopulationSourceSurvival[] Rows(NativePopulationBudget b,int bodies,int main,int final)=>
        b.Sources.Where(s=>s.Active>0).Select(s=>new NativePopulationSourceSurvival(s.Identity,bodies,main,final)).ToArray();
}
