using System.Diagnostics;

namespace EdgeProfileManager.Core.Management;

public interface IProfileCopier
{
    /// <summary>Kopiert einen Ordner rekursiv; ohne <paramref name="includeCaches"/> ohne Cache-Ordner.</summary>
    void Copy(string source, string destination, bool includeCaches);
}

/// <summary>Port von Copy-ProfileFolder: robocopy, unabhängig von PATH.</summary>
public sealed class RobocopyProfileCopier : IProfileCopier
{
    public void Copy(string source, string destination, bool includeCaches)
    {
        var robocopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");
        if (!File.Exists(robocopy))
            throw new FileNotFoundException($"Windows-Kopierprogramm nicht gefunden: {robocopy}. Bitte die Windows-Installation prüfen.");

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

        using var process = Process.Start(start) ?? throw new InvalidOperationException("robocopy konnte nicht gestartet werden.");
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        // robocopy: Exitcodes ab 8 bedeuten Fehler.
        if (process.ExitCode >= 8) throw new IOException($"Robocopy-Fehler ({process.ExitCode}): {output.Result.Trim()}");
    }
}
