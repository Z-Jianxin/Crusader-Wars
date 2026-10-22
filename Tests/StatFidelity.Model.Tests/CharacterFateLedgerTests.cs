using CrusaderWars.StatFidelity;
using Xunit;

public sealed class CharacterFateLedgerTests
{
    [Fact]
    public void WoundedCapturedOutcomeIsResolvedOnceAndCannotBecomeASecondDeathRoll()
    {
        var ledger = new CharacterFateLedger();
        int calls = 0;
        var actual = ledger.Resolve("37088", "\ttraits={ 52 31 }", () => {
            calls++;
            return new ResolvedCharacterFate(false, true, "\ttraits={ 52 31 116 }");
        });
        var second = ledger.Resolve("37088", "\ttraits={ 52 31 }", () => {
            calls++;
            return new ResolvedCharacterFate(true, false, "\ttraits={ 52 31 }");
        });
        Assert.Equal(1, calls);
        Assert.Equal(actual, second);
        Assert.False(ledger.Get("37088").IsSlain);
        Assert.True(ledger.Get("37088").IsCaptured);
        Assert.Contains("116", ledger.Get("37088").TraitsLine);
    }

    [Fact]
    public void MissingOrChangedSourceEvidenceCannotReuseAnotherBattleFate()
    {
        var ledger = new CharacterFateLedger();
        Assert.Throws<InvalidDataException>(() => ledger.Get("1"));
        ledger.Resolve("1", "traits={1}", () => new(false, false, "traits={1 116}"));
        Assert.Throws<InvalidDataException>(() => ledger.Resolve("1", "traits={2}", () => new(false, false, "traits={2}")));
        ledger.Clear();
        Assert.Throws<InvalidDataException>(() => ledger.Get("1"));
    }

    [Fact]
    public void MutuallyExclusiveDeathAndCaptureIsRejected()
    {
        var ledger = new CharacterFateLedger();
        Assert.Throws<InvalidDataException>(() => ledger.Resolve("1", "traits={1}", () => new(true, true, "traits={1}")));
        Assert.Empty(ledger.Entries);
    }
}
