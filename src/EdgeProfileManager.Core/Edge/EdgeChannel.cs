namespace EdgeProfileManager.Core.Edge;

public enum EdgeChannel { Stable, Beta, Dev, Canary }

public static class EdgeChannels
{
    public static IReadOnlyList<EdgeChannel> All { get; } = [EdgeChannel.Stable, EdgeChannel.Beta, EdgeChannel.Dev, EdgeChannel.Canary];

    /// <summary>Verzeichnisname unter %LOCALAPPDATA%\Microsoft bzw. Program Files\Microsoft.</summary>
    public static string DirName(this EdgeChannel channel) => channel switch
    {
        EdgeChannel.Stable => "Edge",
        EdgeChannel.Beta => "Edge Beta",
        EdgeChannel.Dev => "Edge Dev",
        EdgeChannel.Canary => "Edge SxS",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public static bool TryParse(string? value, out EdgeChannel channel) =>
        Enum.TryParse(value, ignoreCase: true, out channel) && Enum.IsDefined(channel);

    /// <summary>Bestimmt den Channel anhand des Pfads von msedge.exe.</summary>
    public static EdgeChannel? FromExecutablePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var channel in All)
        {
            if (path.Contains($@"\Microsoft\{channel.DirName()}\Application\", StringComparison.OrdinalIgnoreCase)) return channel;
        }
        return null;
    }
}

/// <summary>Pfade einer Edge-Installation; für Tests mit anderen Wurzeln überschreibbar.</summary>
public sealed class EdgeEnvironment
{
    public static EdgeEnvironment Default { get; } = new();

    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public IReadOnlyList<string> ProgramRoots { get; init; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ];

    public string UserDataDir(EdgeChannel channel) => Path.Combine(LocalAppData, "Microsoft", channel.DirName(), "User Data");

    public string? FindExecutable(EdgeChannel channel)
    {
        foreach (var root in ProgramRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Microsoft", channel.DirName(), "Application", "msedge.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
