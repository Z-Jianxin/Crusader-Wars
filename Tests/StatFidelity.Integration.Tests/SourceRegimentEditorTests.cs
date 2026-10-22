using CrusaderWars.data.battle_results.editors;
using Xunit;

public sealed class SourceRegimentEditorTests
{
    [Fact]
    public void NativeLevyIndexOneRetainsParentMaximumAndUnselectedChunksByteForByte()
    {
        const string original = "\tregiments={\r\n\t\t854={\r\n\t\t\torigin=1531\r\n\t\t\tmax=191\r\n\t\t\tchunks={ { max=0 current=0 } { max=47 current=44 } }\r\n\t\t}\r\n\t\t999={ size=80 owner=7 source=event }\r\n\t}\r\n";
        string actual = SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("854", 1, "3") });
        Assert.Equal(original.Replace("current=44", "current=3"), actual);
    }

    [Fact]
    public void TwoRaisedChunksOfOneSourceReceiveTheirOwnLosses()
    {
        const string original = "regiments={ 4={ max=300 chunks={ { max=100 current=90 } { max=150 current=130 } { max=50 current=40 } } } }";
        var updates = new[] { new SourceRegimentUpdate("4", 2, "7"), new SourceRegimentUpdate("4", 0, "21") };
        Assert.Equal(original.Replace("current=90", "current=21").Replace("current=40", "current=7"), SourceRegimentEditor.Edit(original, updates));
    }

    [Fact]
    public void NativeDeletedSourceTombstonesArePreservedButCannotBeUpdated()
    {
        const string original = "regiments={ 16488=none 17620={max=400 chunks={ {max=400 current=399} }} 17705=none }";
        Assert.Equal(original.Replace("current=399", "current=51"), SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("17620", 0, "51") }));
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("16488", 0, "0") }));
    }

    [Fact]
    public void CompactEventSourceKeepsAllMetadataIncludingFieldsAfterSize()
    {
        const string original = "regiments={\n 16793701={\n  type=armored_footmen\n  size=600 # native compact size\n  owner=32570\n  source=event\n  uses_supply=no\n  custom={ note=\"quoted } # { \\\" value\" nested={ value=9 } }\n }\n}";
        string actual = SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("16793701", 0, "46") });
        Assert.DoesNotContain("size=600", actual);
        Assert.Contains("max=600", actual);
        Assert.Contains("current=46", actual);
        Assert.Contains("# native compact size\n  owner=32570\n  source=event\n  uses_supply=no\n  custom={ note=\"quoted } # { \\\" value\" nested={ value=9 } }", actual);
        Assert.Equal(actual.Replace("current=46", "current=41"), SourceRegimentEditor.Edit(actual, new[] { new SourceRegimentUpdate("16793701", 0, "41") }));
    }

    [Fact]
    public void ExistingRootMaximumAndMissingDefaultCurrentArePreserved()
    {
        const string original = "7={ max=900 size=600 owner=3 uses_supply=no }\n8={ max=50 chunks={ { max=50 } } }";
        string actual = SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("7", 0, "46"), new SourceRegimentUpdate("8", 0, "0") });
        Assert.Contains("max=900", actual);
        Assert.Contains("max=600", actual);
        Assert.Contains("owner=3 uses_supply=no", actual);
        Assert.Contains("max=50  current=0", actual);
    }

    [Fact]
    public void CommentsAndEscapedQuotesCannotChangeChunkNumbering()
    {
        const string original = "regiments={ 2={ note=\"a \\\" } { # b\" max=20 chunks={ # } { ignored\n { max=10 current=8 text=\"}\" } { max=10 current=9 } } } }";
        Assert.Equal(original.Replace("current=9", "current=2.5"), SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("2", 1, "2.5") }));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("1e1")]
    [InlineData("11")]
    [InlineData("0 } owner=7")]
    public void InvalidOrOutOfBoundsCountsFailBeforeReturningAnyEdit(string current)
    {
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit("1={ size=10 owner=5 }", new[] { new SourceRegimentUpdate("1", 0, current) }));
    }

    [Theory]
    [InlineData("1={ size=10 } 1={ size=10 }")]
    [InlineData("1={ size=10 chunks={ {max=10 current=8} } }")]
    [InlineData("1={ chunks={ {max=10 current=8 current=7} } }")]
    [InlineData("1={ chunks={ {max=10 max=11 current=8} } }")]
    [InlineData("1={ chunks={ named={max=10 current=8} } }")]
    [InlineData("1={ max=10 }")]
    [InlineData("1={ size=10 note=\"unterminated }")]
    [InlineData("1={ chunks={ {max=10 current=8} }")]
    public void AmbiguousOrIncompleteNativeStructureIsRejected(string original)
    {
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("1", 0, "3") }));
    }

    [Fact]
    public void MissingIdentityAndRepeatedRequestsNeverBorrowAnotherChunk()
    {
        const string original = "1={ size=10 }";
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("2", 0, "3") }));
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("1", 1, "3") }));
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit(original, new[] { new SourceRegimentUpdate("1", 0, "3"), new SourceRegimentUpdate("1", 0, "3") }));
        Assert.Equal(original, SourceRegimentEditor.Edit(original, Array.Empty<SourceRegimentUpdate>()));
    }

    [Fact]
    public void SurvivorWriteCannotRegenerateMenEvenBelowMaximum()
    {
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit("1={ max=10 chunks={ {max=10 current=4} } }",
            new[] { new SourceRegimentUpdate("1", 0, "5") }));
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit("1={ max=10 chunks={ {max=10} } }",
            new[] { new SourceRegimentUpdate("1", 0, "1") }));
    }
}
