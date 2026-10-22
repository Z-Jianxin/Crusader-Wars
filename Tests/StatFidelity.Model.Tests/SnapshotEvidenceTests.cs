using CrusaderWars.StatFidelity;
using Xunit;

public class SnapshotEvidenceTests
{
    private static string Save(string serial = "100000", string combat = "123", string extra = "") =>
        "living={\n42={\nfirst_name=\"Brace { in a quoted name\"\nalive_data={variables={data={\n" +
        "{flag=ccstat_snapshot_serial data={type=value identity=" + serial + "}}\n" +
        "{flag=ccstat_combat data={type=com identity=" + combat + "}}\n" + extra + "\n}}}\n}\n}";

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
        Assert.False(SnapshotEvidence.Validate(new StringReader(Save().Replace("type=com", "type=character")), "42", "snapshot-1", "123", out _));
    }

    [Fact]
    public void NativeCombatZeroOmitsIdentityAndStillRequiresExactSelectedCombat()
    {
        // Shape captured verbatim from 1.20.1.2 combat02, snapshot-2:
        // ccstat_combat data={type=com}, ccstat_snapshot_serial identity=200000.
        string native = Save("200000", "0").Replace("type=com identity=0", "type=com");
        Assert.True(SnapshotEvidence.Validate(new StringReader(native), "42", "snapshot-2", "0", out var reason), reason);
        Assert.False(SnapshotEvidence.Validate(new StringReader(native), "42", "snapshot-2", "1", out _));
        Assert.False(SnapshotEvidence.Validate(new StringReader(native), "42", "snapshot-1", "0", out _));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("0.5")]
    [InlineData("0 identity=1")]
    public void MalformedExplicitCombatIdentityDoesNotBecomeDefaultZero(string identity)
    {
        Assert.False(SnapshotEvidence.Validate(new StringReader(Save(combat: identity)), "42", "snapshot-1", "0", out _));
    }

    [Fact]
    public void TruncatedCharacterCannotValidate()
    {
        string save = Save();
        Assert.False(SnapshotEvidence.Validate(new StringReader(save.Substring(0, save.LastIndexOf("\n}\n}", StringComparison.Ordinal))), "42", "snapshot-1", "123", out _));
    }
}
