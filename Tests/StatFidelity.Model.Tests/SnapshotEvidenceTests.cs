using CrusaderWars.StatFidelity;
using Xunit;

public class SnapshotEvidenceTests
{
    private static string Save(string serial = "100000", string combat = "123", string extra = "") =>
        "living={\n42={\nfirst_name=\"Brace { in a quoted name\"\nalive_data={variables={data={\n" +
        "{flag=ccstat_snapshot_serial data={type=value identity=" + serial + "}}\n" +
        "{flag=ccstat_combat data={type=combat identity=" + combat + "}}\n" + extra + "\n}}}\n}\n}";

    [Fact]
    public void CurrentEngineFixedPointSerialMatchesSelectedCombat()
    {
        Assert.True(SnapshotEvidence.Validate(new StringReader(Save()), "42", "snapshot-1", "123", out var reason), reason);
        Assert.Equal(100000, SnapshotEvidence.SavedValueScale);
    }

    [Theory]
    [InlineData("42", "snapshot-2", "123")]
    [InlineData("42", "snapshot-1", "124")]
    [InlineData("43", "snapshot-1", "123")]
    [InlineData("42", "battle-1", "123")]
    [InlineData("42", "snapshot-999999999999999999999", "123")]
    public void StaleOrUnboundSnapshotsAreRejected(string player, string serial, string combat)
    {
        Assert.False(SnapshotEvidence.Validate(new StringReader(Save()), player, serial, combat, out var reason));
        Assert.Contains("rejected", reason);
    }

    [Fact]
    public void MissingOrDuplicateSerialIsNotAccepted()
    {
        Assert.False(SnapshotEvidence.Validate(new StringReader(Save("1")), "42", "snapshot-1", "123", out _));
        Assert.False(SnapshotEvidence.Validate(new StringReader(Save(extra: "{flag=ccstat_snapshot_serial data={type=value identity=100000}}")), "42", "snapshot-1", "123", out _));
    }

    [Fact]
    public void WrongScopeTypeIsNotAssumedToBeCombat()
    {
        Assert.False(SnapshotEvidence.Validate(new StringReader(Save().Replace("type=combat", "type=character")), "42", "snapshot-1", "123", out _));
    }

    [Fact]
    public void TruncatedCharacterCannotValidate()
    {
        string save = Save();
        Assert.False(SnapshotEvidence.Validate(new StringReader(save.Substring(0, save.LastIndexOf("\n}\n}", StringComparison.Ordinal))), "42", "snapshot-1", "123", out _));
    }
}
