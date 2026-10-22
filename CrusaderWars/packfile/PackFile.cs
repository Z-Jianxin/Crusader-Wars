using System;
using System.IO;
using CrusaderWars.StatFidelity;

namespace CrusaderWars
{
    public static class PackFile
    {
        public static void PackFileCreator()
        {
            string runtimeRoot = Directory.GetCurrentDirectory();
            string rpfm = Path.Combine(runtimeRoot, "data", "rpfm", "rpfm_cli.exe");
            string schema = Path.Combine(runtimeRoot, "data", "schema_att.ron");
            string sources = Path.Combine(runtimeRoot, "data", "battle files");
            string attila = Properties.Settings.Default.VAR_attila_path;
            string destinationDirectory = Path.Combine(Path.GetDirectoryName(attila)!, "data");
            string destination = Path.Combine(destinationDirectory, "CrusaderConflicts.pack");

            StatFidelitySession.WriteCoverageReport(runtimeRoot);
            StatFidelityBackend.ClearGeneratedTables(runtimeRoot);
            if (StatFidelitySession.PendingProfiles.Count > 0)
            {
                Program.Logger.Debug($"Generating {StatFidelitySession.PendingProfiles.Count} stat fidelity profiles...");
                try
                {
                    if (StatFidelitySession.PreparedDatabase is PreparedAttilaDatabase prepared)
                        prepared.Generate(StatFidelitySession.PendingProfiles, sources);
                    else StatFidelityBackend.Generate(runtimeRoot, attila, StatFidelitySession.PendingProfiles);
                    GeneratedUnitLabels.Apply(sources);
                }
                catch (Exception ex)
                {
                    StatFidelitySession.Note("Attila stat generation failed: " + ex.Message);
                    StatFidelitySession.WriteCoverageReport(runtimeRoot);
                    throw;
                }
            }
            Directory.CreateDirectory(destinationDirectory);
            // Build beside the destination: the final atomic replacement never crosses volumes.
            string staging = Path.Combine(destinationDirectory, "cc-building-" + Guid.NewGuid().ToString("N") + ".pack");
            try
            {
                RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", staging);
                RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", staging, "-t", schema, "-F", sources);
                if (!File.Exists(staging) || new FileInfo(staging).Length < 28)
                    throw new InvalidOperationException("RPFM produced an empty battle pack. The previous battle pack is unchanged.");
                RpfmRunner.Run(rpfm, "--game", "attila", "pack", "list", "-p", staging);
                if (File.Exists(destination)) File.Replace(staging, destination, null);
                else File.Move(staging, destination);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            string thumbnail = Path.Combine(destinationDirectory, "CrusaderWars.png");
            if (!File.Exists(thumbnail)) File.Copy(Path.Combine(runtimeRoot, "settings", "CrusaderWars.png"), thumbnail);
            Program.Logger.Debug("Battle pack created and atomically installed successfully.");
        }
    }
}
