using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Core.Config;

/// <summary>Lädt und speichert config.json und meldet Änderungen an der Datei.</summary>
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

    public ConfigStore(string path) => Path = path;

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdgeProfileManager", "config.json");

    public string Path { get; }

    /// <summary>Wird nach einer Änderung an der Datei ausgelöst (entprellt, auf einem Threadpool-Thread).</summary>
    public event Action? Changed;

    public static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? throw new JsonException("Konfiguration ist leer.");

    /// <summary>Lädt die Konfiguration. Fehlt die Datei, wird eine Standardkonfiguration erzeugt und gespeichert.</summary>
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
        File.WriteAllText(Path, JsonSerializer.Serialize(config, JsonOptions));
    }

    public void Watch()
    {
        if (_watcher is not null) return;
        _debounce = new Timer(_ => Changed?.Invoke());
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

    /// <summary>Standard: eine Gruppe pro Edge-Channel mit allen Profilen.</summary>
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
