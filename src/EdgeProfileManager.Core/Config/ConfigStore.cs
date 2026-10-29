using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Core.Resources;

namespace EdgeProfileManager.Core.Config;

/// <summary>Loads and saves config.json and reports changes to the file.</summary>
public sealed class ConfigStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastWritten;

    public ConfigStore(string path) => Path = path;

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdgeProfileManager", "config.json");

    public string Path { get; }

    /// <summary>
    /// Raised after the file changes (debounced, on a thread pool thread);
    /// not for changes written by <see cref="Save"/> itself.
    /// </summary>
    public event Action? Changed;

    public static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? throw new JsonException(Strings.Config_Empty);

    /// <summary>The configured UI language, read before the application starts; null if not set or unreadable.</summary>
    public static string? ReadLanguage(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)).Language : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Loads the configuration. If the file is missing, a default configuration is created and saved.</summary>
    public AppConfig LoadOrCreate(Func<AppConfig> createDefault)
    {
        if (!File.Exists(Path))
        {
            var config = createDefault();
            Save(config);
            return config;
        }
        return Parse(File.ReadAllText(Path));
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        _lastWritten = json;
        File.WriteAllText(Path, json);
    }

    public void Watch()
    {
        if (_watcher is not null) return;
        _debounce = new Timer(_ =>
        {
            if (!IsOwnWrite()) Changed?.Invoke();
        });
        _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler handler = (_, _) => _debounce.Change(500, Timeout.Infinite);
        _watcher.Changed += handler;
        _watcher.Created += handler;
        _watcher.Renamed += (_, _) => _debounce.Change(500, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    private bool IsOwnWrite()
    {
        try { return _lastWritten is not null && File.ReadAllText(Path) == _lastWritten; }
        catch (IOException) { return false; }
    }

    /// <summary>Default: one group per Edge channel containing all its profiles.</summary>
    public static AppConfig CreateDefault(IEnumerable<EdgeProfile> profiles) => new()
    {
        Menu = profiles
            .GroupBy(p => p.Channel)
            .Select(g => new MenuEntry
            {
                Group = $"Edge {g.Key}",
                Children = g.Select(p => new MenuEntry { Profile = p.Key }).ToList(),
            })
            .ToList(),
    };

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
