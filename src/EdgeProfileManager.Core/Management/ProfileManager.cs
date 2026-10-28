using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Core.Management;

/// <summary>Optionen für Backup, Restore, Kopieren und Verschieben (entsprechen den Script-Schaltern).</summary>
public sealed record TransferOptions
{
    /// <summary>Nur lokale Anwendungsdaten (Local Storage, IndexedDB, …) statt des ganzen Profils (-LocalOnly).</summary>
    public bool LocalOnly { get; init; }

    /// <summary>Cache-Ordner mitkopieren (-IncludeCaches).</summary>
    public bool IncludeCaches { get; init; }

    /// <summary>Quelle darf geöffnet sein; nur für Backup und Kopieren (-BackupWhileRunning).</summary>
    public bool AllowRunningSource { get; init; }

    /// <summary>Vorhandenes Zielprofil vollständig ersetzen (-Force beim Restore/Kopieren).</summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>
/// Profilverwaltung, portiert aus EdgeProfile.ps1: Neu, Umbenennen, Avatar, Löschen, Backup,
/// Restore, Kopieren und geprüftes Verschieben zwischen Edge-Instanzen.
/// </summary>
public sealed class ProfileManager(EdgeEnvironment environment, IEdgeProcessGuard guard, IProfileCopier copier)
{
    public static readonly string[] CacheDirs =
        ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", "DawnCache", "GrShaderCache", "ShaderCache"];

    public static readonly string[] LocalDataDirs =
        ["Local Storage", "IndexedDB", "Session Storage", "Service Worker", "Local Extension Settings", "Extension State"];

    private const string AvatarFileName = "Google Profile Picture.png";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly ProfileDiscovery _discovery = new(environment);

    public event Action<string>? Log;

    public EdgeProfile GetProfile(EdgeChannel channel, string profile) =>
        _discovery.GetProfiles(channel).FirstOrDefault(p => p.Folder == profile || p.Name == profile)
        ?? throw new InvalidOperationException($"Profil '{profile}' in {channel} nicht gefunden.");

    public string NextProfileFolder(EdgeChannel channel)
    {
        var userData = environment.UserDataDir(channel);
        var i = 1;
        while (Directory.Exists(Path.Combine(userData, $"Profile {i}"))) i++;
        return $"Profile {i}";
    }

    // ---------- Neu, Umbenennen, Avatar, Löschen ----------

    public EdgeProfile Create(EdgeChannel channel, string name, string account, string? avatarPath = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("Zum Erstellen sind Profilname und E-Mail erforderlich.");
        if (!string.IsNullOrWhiteSpace(avatarPath)) avatarPath = ValidateAvatar(avatarPath);
        AssertClosed(channel);
        var userData = environment.UserDataDir(channel);
        if (!Directory.Exists(userData) || LoadLocalState(channel) is null) throw new InvalidOperationException($"Edge {channel} zuerst einmal starten.");
        if (_discovery.GetProfiles(channel).Any(p => p.Name == name)) throw new InvalidOperationException($"Profil '{name}' in {channel} existiert bereits.");

        var folder = NextProfileFolder(channel);
        var destination = Path.Combine(userData, folder);
        Directory.CreateDirectory(destination);
        WriteJson(Path.Combine(destination, "customer-profile.json"), new JsonObject { ["User"] = account }, indented: true);
        WriteJson(Path.Combine(destination, "Preferences"), new JsonObject { ["profile"] = new JsonObject { ["name"] = name } });
        Register(channel, folder, new JsonObject { ["name"] = name, ["is_using_default_name"] = false });
        if (!string.IsNullOrWhiteSpace(avatarPath)) SetAvatar(channel, folder, avatarPath);
        Log?.Invoke($"Profil '{name}' ({channel}/{folder}) erstellt. Zur Synchronisierung bei Edge als {account} anmelden.");
        return GetProfile(channel, folder);
    }

    public void Rename(EdgeChannel channel, string profile, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Neuer Name fehlt.");
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        var preferencesFile = Path.Combine(p.Path, "Preferences");
        if (!File.Exists(preferencesFile)) throw new InvalidOperationException($"Profil '{profile}' wurde von Edge noch nicht initialisiert.");
        var state = LoadLocalState(channel);
        if (state?["profile"]?["info_cache"]?[p.Folder] is not JsonObject info) throw new InvalidOperationException($"Profil '{profile}' ist nicht registriert.");

        var preferences = ReadJsonObject(preferencesFile);
        if (preferences["profile"] is not JsonObject profileNode) preferences["profile"] = profileNode = new JsonObject();
        profileNode["name"] = newName;
        info["name"] = newName;
        info["is_using_default_name"] = false;
        WriteJson(preferencesFile, preferences);
        SaveLocalState(channel, state);
        Log?.Invoke($"Profil '{p.Folder}' in Edge {channel} heisst jetzt '{newName}'.");
    }

    public void SetAvatar(EdgeChannel channel, string profile, string imagePath)
    {
        var image = ValidateAvatar(imagePath);
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        File.Copy(image, Path.Combine(p.Path, AvatarFileName), overwrite: true);
        var state = LoadLocalState(channel);
        if (state?["profile"]?["info_cache"]?[p.Folder] is not JsonObject info) throw new InvalidOperationException($"Profil '{profile}' ist nicht in Local State registriert.");
        info["gaia_picture_file_name"] = AvatarFileName;
        info["use_gaia_picture"] = true;
        info["is_using_default_avatar"] = false;
        info["last_downloaded_gaia_picture_url_with_size"] = "";
        SaveLocalState(channel, state);
        Log?.Invoke($"Eigenes Avatar für Edge {channel}/{p.Folder} gesetzt.");
    }

    /// <summary>
    /// Setzt die Profilfarbe wie "Darstellung › Farbe" in Edge: Farbdesign im Profil (Preferences)
    /// sowie Avatar- und Hervorhebungsfarbe in Local State. Farbe als 0xRRGGBB.
    /// </summary>
    public void SetColor(EdgeChannel channel, string profile, int rgb)
    {
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        var preferencesFile = Path.Combine(p.Path, "Preferences");
        if (!File.Exists(preferencesFile)) throw new InvalidOperationException($"Profil '{profile}' wurde von Edge noch nicht initialisiert.");
        var state = LoadLocalState(channel);
        if (state?["profile"]?["info_cache"]?[p.Folder] is not JsonObject info) throw new InvalidOperationException($"Profil '{profile}' ist nicht registriert.");

        // Chromium speichert Farben als vorzeichenbehaftetes ARGB (SkColor).
        var argb = unchecked((int)(0xFF000000u | ((uint)rgb & 0xFFFFFFu)));
        var preferences = ReadJsonObject(preferencesFile);
        GetOrCreate(GetOrCreate(preferences, "extensions"), "theme")["id"] = "autogenerated_theme_id";
        GetOrCreate(GetOrCreate(preferences, "autogenerated"), "theme")["color"] = argb;
        info["profile_highlight_color"] = argb;
        info["default_avatar_fill_color"] = argb;
        WriteJson(preferencesFile, preferences);
        SaveLocalState(channel, state);
        Log?.Invoke($"Farbe #{rgb & 0xFFFFFF:X6} für Edge {channel}/{p.Folder} gesetzt.");
    }

    public void Delete(EdgeChannel channel, string profile)
    {
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        Directory.Delete(p.Path, recursive: true);
        var state = LoadLocalState(channel);
        if (state?["profile"] is JsonObject profileNode)
        {
            (profileNode["info_cache"] as JsonObject)?.Remove(p.Folder);
            RemoveFromArray(profileNode, "profiles_order", p.Folder);
            RemoveFromArray(profileNode, "last_active_profiles", p.Folder);
            if ((string?)profileNode["last_used"] == p.Folder) profileNode.Remove("last_used");
            SaveLocalState(channel, state);
        }
        Log?.Invoke($"Profil '{p.Name}' aus Edge {channel} gelöscht.");
    }

    // ---------- Backup und Restore ----------

    public string Backup(EdgeChannel channel, string profile, string destinationDir, TransferOptions options)
    {
        AssertClosed(channel, options.AllowRunningSource);
        var p = GetProfile(channel, profile);
        Log?.Invoke($"Sichere Edge {channel} – {p.Name} ({p.Folder}) …");
        Directory.CreateDirectory(destinationDir);
        var temp = Path.Combine(Path.GetTempPath(), "EdgeBk_" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var staged = Path.Combine(temp, "Profile");
            if (options.LocalOnly)
            {
                Directory.CreateDirectory(staged);
                CopyLocalData(p.Path, staged, options.IncludeCaches);
            }
            else
            {
                copier.Copy(p.Path, staged, options.IncludeCaches);
            }
            var sourceInfo = LoadLocalState(channel)?["profile"]?["info_cache"]?[p.Folder]?.DeepClone();
            var meta = new JsonObject
            {
                ["Channel"] = channel.ToString(),
                ["Folder"] = p.Folder,
                ["Name"] = p.Name,
                ["Computer"] = Environment.MachineName,
                ["User"] = Environment.UserName,
                ["Date"] = DateTime.Now.ToString("s"),
                ["InfoCache"] = options.LocalOnly
                    ? new JsonObject { ["name"] = p.Name, ["user_name"] = sourceInfo?["user_name"]?.DeepClone() }
                    : sourceInfo,
            };
            WriteJson(Path.Combine(temp, "meta.json"), meta, indented: true);
            var safeName = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '_' : c));
            var zip = Path.Combine(destinationDir, $"Edge_{channel}_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(temp, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            Log?.Invoke($"Backup erstellt: {zip}");
            Log?.Invoke("Hinweis: Gespeicherte Passwörter/Cookies sind per DPAPI verschlüsselt und auf einem anderen PC/Benutzer nicht lesbar.");
            return zip;
        }
        finally { TryDeleteDirectory(temp); }
    }

    /// <summary>Stellt ein Backup wieder her; ohne <paramref name="targetFolder"/> als neues Profil. Liefert den Zielordner.</summary>
    public string Restore(EdgeChannel channel, string zipPath, string? targetFolder, TransferOptions options)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException($"Backup-Datei '{zipPath}' nicht gefunden.");
        AssertClosed(channel);
        var userData = environment.UserDataDir(channel);
        if (!Directory.Exists(userData)) throw new InvalidOperationException($"Edge {channel} ist nicht installiert bzw. wurde nie gestartet.");

        var temp = Path.Combine(Path.GetTempPath(), "EdgeRs_" + Guid.NewGuid());
        try
        {
            Log?.Invoke($"Entpacke Backup: {zipPath}");
            ZipFile.ExtractToDirectory(zipPath, temp);
            var meta = ReadJsonObject(Path.Combine(temp, "meta.json"));
            var name = (string?)meta["Name"] ?? "Wiederhergestellt";
            if (string.IsNullOrEmpty(targetFolder)) targetFolder = NextProfileFolder(channel);
            var destination = Path.Combine(userData, targetFolder);
            var source = Path.Combine(temp, "Profile");

            if (options.LocalOnly)
            {
                ImportLocalData(channel, source, targetFolder, name, (string?)meta["InfoCache"]?["user_name"], options.IncludeCaches);
            }
            else
            {
                ReplaceTarget(destination, targetFolder, options);
                Log?.Invoke($"Kopiere Profil nach Edge {channel}/{targetFolder} …");
                copier.Copy(source, destination, includeCaches: true);
                Register(channel, targetFolder, meta["InfoCache"]?.DeepClone());
                Log?.Invoke($"Profil '{name}' nach Edge {channel}/{targetFolder} wiederhergestellt.");
            }
            return targetFolder;
        }
        finally { TryDeleteDirectory(temp); }
    }

    // ---------- Kopieren und Verschieben ----------

    /// <summary>Port von Move-EdgeProfile: kopiert ein Profil in eine andere Instanz. Liefert den Zielordner.</summary>
    public string Copy(EdgeChannel from, EdgeChannel to, string profile, string? targetFolder, TransferOptions options)
    {
        if (from == to) throw new InvalidOperationException("Quelle und Ziel sind identisch.");
        AssertClosed(from, options.AllowRunningSource);
        AssertClosed(to);
        var p = GetProfile(from, profile);
        var userData = environment.UserDataDir(to);
        if (!Directory.Exists(userData)) throw new InvalidOperationException($"Edge {to} ist nicht installiert bzw. wurde nie gestartet.");
        if (string.IsNullOrEmpty(targetFolder)) targetFolder = NextProfileFolder(to);
        var destination = Path.Combine(userData, targetFolder);

        if (options.LocalOnly)
        {
            var account = (string?)LoadLocalState(from)?["profile"]?["info_cache"]?[p.Folder]?["user_name"];
            ImportLocalData(to, p.Path, targetFolder, p.Name, account, options.IncludeCaches);
            return targetFolder;
        }

        ReplaceTarget(destination, targetFolder, options);
        Log?.Invoke($"Übertrage {p.Name} von Edge {from} nach {to}/{targetFolder} …");
        copier.Copy(p.Path, destination, options.IncludeCaches);
        Register(to, targetFolder, LoadLocalState(from)?["profile"]?["info_cache"]?[p.Folder]?.DeepClone());
        Log?.Invoke($"Profil '{p.Name}' von {from} nach {to}/{targetFolder} kopiert.");
        return targetFolder;
    }

    /// <summary>
    /// Port von Move-EdgeProfileVerified: kopiert, prüft jede Datei per SHA256 und löscht die
    /// Quelle erst danach. Liefert den neuen Zielordner.
    /// </summary>
    public string MoveVerified(EdgeChannel from, EdgeChannel to, string profile, TransferOptions options)
    {
        if (from == to) throw new InvalidOperationException("Quelle und Ziel sind identisch.");
        if (options.LocalOnly || options.AllowRunningSource)
            throw new InvalidOperationException("Verschieben erfordert ein vollständiges Profil und geschlossene Edge-Instanzen.");
        AssertClosed(from);
        AssertClosed(to);
        var source = GetProfile(from, profile);
        if (LoadLocalState(to) is null) throw new InvalidOperationException($"Edge {to} zuerst einmal starten.");
        if (Directory.EnumerateFileSystemEntries(source.Path, "*", SearchOption.AllDirectories)
            .Any(entry => File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidOperationException("Profile mit symbolischen Links oder Verzeichnisverknüpfungen werden aus Sicherheitsgründen nicht verschoben.");

        var folder = NextProfileFolder(to);
        var destination = Path.Combine(environment.UserDataDir(to), folder);
        Log?.Invoke("Erstelle Prüfsummen der Quelle …");
        var manifest = GetRelevantFiles(source.Path, options.IncludeCaches).ToDictionary(r => r, r => Hash(Path.Combine(source.Path, r)), StringComparer.OrdinalIgnoreCase);

        Copy(from, to, source.Folder, folder, options with { ReplaceExisting = false });
        if (LoadLocalState(to)?["profile"]?["info_cache"]?[folder] is null) throw new InvalidOperationException("Zielprofil nicht registriert. Quelle bleibt erhalten.");
        AssertClosed(from);
        AssertClosed(to);

        Log?.Invoke("Prüfe Zielkopie …");
        if (GetRelevantFiles(source.Path, options.IncludeCaches).Count() != manifest.Count)
            throw new InvalidOperationException("Quelldateien haben sich geändert. Quelle bleibt erhalten.");
        foreach (var (relative, hash) in manifest)
        {
            var target = Path.Combine(destination, relative);
            if (!File.Exists(target) || Hash(target) != hash || Hash(Path.Combine(source.Path, relative)) != hash)
                throw new InvalidOperationException($"Prüfung fehlgeschlagen: {relative}. Quelle bleibt erhalten.");
        }
        if (guard.IsRunning(from) || guard.IsRunning(to)) throw new InvalidOperationException("Edge wurde während der Prüfung gestartet. Quelle bleibt erhalten.");

        Delete(from, source.Folder);
        Log?.Invoke($"Profil nach {to}/{folder} verschoben; Zielkopie mit SHA256 geprüft.");
        return folder;
    }

    // ---------- Hilfsfunktionen ----------

    private void AssertClosed(EdgeChannel channel, bool allowRunning = false)
    {
        if (!guard.IsRunning(channel)) return;
        if (!allowRunning) throw new EdgeRunningException(channel);
        Log?.Invoke($"Warnung: Edge {channel} läuft noch. Die Profilkopie kann unvollständig oder inkonsistent sein.");
    }

    private void ReplaceTarget(string destination, string targetFolder, TransferOptions options)
    {
        if (!Directory.Exists(destination)) return;
        if (!options.ReplaceExisting) throw new InvalidOperationException($"Zielprofil '{targetFolder}' existiert bereits. Zum Ersetzen bestätigen.");
        Directory.Delete(destination, recursive: true);
    }

    /// <summary>Gemeinsamer Teil von Restore/Kopieren mit "nur lokale Daten" (neues oder bestehendes Zielprofil).</summary>
    private void ImportLocalData(EdgeChannel channel, string source, string targetFolder, string name, string? account, bool includeCaches)
    {
        var destination = Path.Combine(environment.UserDataDir(channel), targetFolder);
        var isNew = !Directory.Exists(destination);
        if (!isNew && !File.Exists(Path.Combine(destination, "Preferences")))
            throw new InvalidOperationException($"Zielordner '{targetFolder}' ist kein vorhandenes Edge-Profil.");
        if (isNew && LoadLocalState(channel) is null)
            throw new InvalidOperationException($"Keine 'Local State' Datei in {channel} gefunden. Edge einmal starten.");

        Log?.Invoke($"Übertrage lokale Daten nach Edge {channel}/{targetFolder} …");
        Directory.CreateDirectory(destination);
        CopyLocalData(source, destination, includeCaches);
        if (isNew)
        {
            Register(channel, targetFolder, new JsonObject { ["name"] = name });
            if (!string.IsNullOrEmpty(account)) Log?.Invoke($"Konto für die erneute Anmeldung: {account}");
            Log?.Invoke($"Neues Profil ohne Anmeldung angelegt. Edge {channel}/{targetFolder} starten und anmelden, um die Synchronisierung zu aktivieren.");
        }
        Log?.Invoke($"Lokale Daten nach Edge {channel}/{targetFolder} übertragen.");
    }

    private void CopyLocalData(string source, string destination, bool includeCaches)
    {
        foreach (var directory in LocalDataDirs)
        {
            var from = Path.Combine(source, directory);
            if (!Directory.Exists(from)) continue;
            Log?.Invoke($"  Kopiere {directory} …");
            copier.Copy(from, Path.Combine(destination, directory), includeCaches);
        }
    }

    private void Register(EdgeChannel channel, string folder, JsonNode? infoCache)
    {
        var state = LoadLocalState(channel);
        if (state is null)
        {
            Log?.Invoke($"Warnung: Keine 'Local State' Datei in {channel} gefunden – Edge einmal starten.");
            return;
        }
        if (state["profile"] is not JsonObject profileNode) state["profile"] = profileNode = new JsonObject();
        if (profileNode["info_cache"] is not JsonObject cache) profileNode["info_cache"] = cache = new JsonObject();
        cache[folder] = infoCache ?? new JsonObject { ["name"] = folder };
        if (profileNode["profiles_order"] is JsonArray order && !order.Any(n => (string?)n == folder)) order.Add(folder);
        SaveLocalState(channel, state);
    }

    private JsonObject? LoadLocalState(EdgeChannel channel) =>
        ProfileDiscovery.ReadLocalState(environment.UserDataDir(channel)) as JsonObject;

    private void SaveLocalState(EdgeChannel channel, JsonObject state) =>
        WriteJson(Path.Combine(environment.UserDataDir(channel), "Local State"), state);

    private static JsonObject GetOrCreate(JsonObject parent, string property)
    {
        if (parent[property] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[property] = created;
        return created;
    }

    private static void RemoveFromArray(JsonObject parent, string property, string value)
    {
        if (parent[property] is not JsonArray array) return;
        foreach (var node in array.Where(n => (string?)n == value).ToList()) array.Remove(node);
    }

    private static IEnumerable<string> GetRelevantFiles(string root, bool includeCaches) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file))
            .Where(relative => includeCaches || !CacheDirs.Any(dir => relative.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)));

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string ValidateAvatar(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) throw new FileNotFoundException($"Avatar-Datei '{imagePath}' nicht gefunden.");
        if (!string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Das Profilavatar muss eine PNG-Datei sein.");
        var signature = new byte[8];
        using (var stream = File.OpenRead(imagePath))
        {
            if (stream.ReadAtLeast(signature, 8, throwOnEndOfStream: false) != 8 || !signature.SequenceEqual(PngSignature))
                throw new ArgumentException("Die Avatar-Datei ist kein gültiges PNG.");
        }
        return Path.GetFullPath(imagePath);
    }

    private static JsonObject ReadJsonObject(string file) =>
        JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? throw new InvalidDataException($"{file} enthält kein JSON-Objekt.");

    private static void WriteJson(string file, JsonNode node, bool indented = false) =>
        File.WriteAllText(file, indented ? node.ToJsonString(IndentedJson) : node.ToJsonString(), new UTF8Encoding(false));

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
