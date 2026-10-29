using System.Diagnostics;
using System.Globalization;
using EdgeProfileManager.Core.Resources;

namespace EdgeProfileManager.Core.Management;

public interface IProfileCopier
{
    /// <summary>Copies a folder recursively; without <paramref name="includeCaches"/>, cache folders are skipped.</summary>
    void Copy(string source, string destination, bool includeCaches);
}

/// <summary>Copies with robocopy from the system directory, independent of PATH.</summary>
public sealed class RobocopyProfileCopier : IProfileCopier
{
    public void Copy(string source, string destination, bool includeCaches)
    {
        var robocopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");
        if (!File.Exists(robocopy))
            throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_RobocopyNotFound, robocopy));

        var start = new ProcessStartInfo(robocopy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { source, destination, "/E", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP" })
            start.ArgumentList.Add(argument);
        if (!includeCaches)
        {
            start.ArgumentList.Add("/XD");
            foreach (var directory in ProfileManager.CacheDirs) start.ArgumentList.Add(Path.Combine(source, directory));
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(Strings.Error_RobocopyNotStarted);
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        // robocopy: exit codes of 8 and above indicate errors.
        if (process.ExitCode >= 8) throw new IOException(string.Format(CultureInfo.CurrentCulture, Strings.Error_Robocopy, process.ExitCode, output.Result.Trim()));
    }
}
