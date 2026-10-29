using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace EdgeProfileManager;

/// <summary>Updates via Velopack from the GitHub releases; only available in the installed version.</summary>
public sealed class Updater
{
    public const string RepositoryUrl = "https://github.com/brasselit/EdgeProfileManager";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, null, prerelease: false));

    /// <summary>False when started from a build or publish folder instead of an installation.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string Version => _manager.CurrentVersion?.ToString()
        ?? typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    /// <summary>Newer release, or null if up to date or not installed.</summary>
    public async Task<UpdateInfo?> CheckAsync() => IsInstalled ? await _manager.CheckForUpdatesAsync() : null;

    /// <summary>Downloads the update, then restarts into the new version in the tray.</summary>
    public async Task InstallAsync(UpdateInfo update)
    {
        await _manager.DownloadUpdatesAsync(update);
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease, ["--background"]);
    }
}
