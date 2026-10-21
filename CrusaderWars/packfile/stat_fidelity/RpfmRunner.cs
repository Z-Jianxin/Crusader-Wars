using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CrusaderWars.StatFidelity;

public static class RpfmRunner
{
    public static string Run(string executable, params string[] arguments)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("RPFM CLI is missing. Restore the Crusader Conflicts runtime data.", executable);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start RPFM.");
        // Drain both streams concurrently: a full stderr pipe must never hang battle creation.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300000))
        {
            process.Kill(true);
            throw new TimeoutException("RPFM did not finish within five minutes. The existing battle pack was preserved.");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"RPFM failed ({process.ExitCode}): {stderr.Result}\n{stdout.Result}");
        return stdout.Result;
    }
}
