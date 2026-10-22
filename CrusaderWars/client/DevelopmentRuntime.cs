using System.IO;

namespace CrusaderWars.client
{
    /// <summary>An explicit marker for a separately staged development runtime.</summary>
    public static class DevelopmentRuntime
    {
        public const string MarkerFileName = "development-runtime.json";
        public static bool Enabled => File.Exists(Path.Combine(Directory.GetCurrentDirectory(), MarkerFileName));
        public static string VersionSuffix => Enabled ? " (development)" : "";
    }
}
