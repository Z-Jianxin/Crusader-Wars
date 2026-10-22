using CrusaderWars;
using CrusaderWars.client;
using Xunit;

public sealed class DevelopmentRuntimeTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-development-tests-" + Guid.NewGuid().ToString("N"));

    public DevelopmentRuntimeTests()
    {
        Directory.CreateDirectory(scratch);
        Environment.CurrentDirectory = scratch;
    }

    [Fact]
    public async Task ExplicitMarkerSkipsUpstreamReplacementAndPreservesNormalVersions()
    {
        Assert.False(DevelopmentRuntime.Enabled);
        File.WriteAllText(DevelopmentRuntime.MarkerFileName, "{\"purpose\":\"test\"}");
        File.WriteAllText("app_version.txt", "version=\"1.4.2\"");
        File.WriteAllText("um_version.txt", "version=\"1.2.3\"");
        Assert.True(DevelopmentRuntime.Enabled);
        Assert.Contains("development", DevelopmentRuntime.VersionSuffix);
        var updater = new Updater();
        await updater.CheckAppVersion();
        await updater.CheckUnitMappersVersion();
        Assert.Equal("1.4.2", updater.AppVersion);
        Assert.Equal("1.2.3", updater.UMVersion);
        Assert.Equal("version=\"1.4.2\"", File.ReadAllText("app_version.txt"));
        Assert.False(Directory.Exists("data/updater"));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = previous;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
