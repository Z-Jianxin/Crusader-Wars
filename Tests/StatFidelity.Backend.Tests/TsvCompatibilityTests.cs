using CrusaderWars.StatFidelity;
using Xunit;

public sealed class TsvCompatibilityTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LiteralLoneCarriageReturnInsideRpfmFieldIsPreserved(string newline)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cc-tsv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "source.tsv");
            File.WriteAllText(path, "key\tanimation\tvalue" + newline + "#land_units_tables;30;db/land_units_tables/source\t\t" + newline +
                "first\tanimation_id\r\t5" + newline + "second\tvalid\t6" + newline);
            var table = TsvTable.Read(path);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal("animation_id\r", table.Rows[0]["animation"]);
            string roundtrip = Path.Combine(directory, "roundtrip.tsv");
            table.Write(roundtrip);
            Assert.Equal("animation_id\r", TsvTable.Read(roundtrip).Rows[0]["animation"]);
            Assert.Equal("6", table.Rows[1]["value"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TruncatedRpfmRecordStillFailsInsteadOfJoiningRows()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cc-tsv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "source.tsv");
            File.WriteAllText(path, "key\tvalue\n#test_tables;1;db/test_tables/source\t\nfirst\nsecond\t2\n");
            Assert.Throws<InvalidDataException>(() => TsvTable.Read(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
