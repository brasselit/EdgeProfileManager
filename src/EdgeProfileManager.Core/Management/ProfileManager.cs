using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Core.Management;

/// <summary>Options for backup, restore, copy and move.</summary>
public sealed record TransferOptions
{
    /// <summary>Only local app data (Local Storage, IndexedDB, …) instead of the whole profile.</summary>
    public bool LocalOnly { get; init; }

    /// <summary>Also copy cache folders.</summary>
    public bool IncludeCaches { get; init; }

    /// <summary>Source may be open; only for backup and copy.</summary>
    public bool AllowRunningSource { get; init; }

    /// <summary>Fully replace an existing target profile (restore/copy).</summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>
/// Profile management: create, rename, avatar, color, delete, backup, restore,
/// copy and verified move between Edge channels.
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

    /// <summary>
    /// Looks up a profile by folder first, then by display name: Edge names new profiles "Profile 1" etc.,
    /// so a name can also be the folder of another profile.
    /// </summary>
    public EdgeProfile GetProfile(EdgeChannel channel, string profile)
    {
        var profiles = _discovery.GetProfiles(channel);
        return profiles.FirstOrDefault(p => string.Equals(p.Folder, profile, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(p => p.Name == profile)
            ?? throw new InvalidOperationException($"Profile '{profile}' not found in {channel}.");
    }

    public string NextProfileFolder(EdgeChannel channel)
    {
        var userData = environment.UserDataDir(channel);
        var i = 1;
        while (Directory.Exists(Path.Combine(userData, $"Profile {i}"))) i++;
        return $"Profile {i}";
    }

    // ---------- Create, rename, avatar, delete ----------

    public EdgeProfile Create(EdgeChannel channel, string name, string account, string? avatarPath = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("Profile name and email are required to create a profile.");
        if (!string.IsNullOrWhiteSpace(avatarPath)) avatarPath = ValidateAvatar(avatarPath);
        AssertClosed(channel);
        var userData = environment.UserDataDir(channel);
        if (!Directory.Exists(userData) || LoadLocalState(channel) is null) throw new InvalidOperationException($"Start Edge {channel} once first.");
        if (_discovery.GetProfiles(channel).Any(p => p.Name == name)) throw new InvalidOperationException($"Profile '{name}' already exists in {channel}.");

        var folder = NextProfileFolder(channel);
        var destination = Path.Combine(userData, folder);
        Directory.CreateDirectory(destination);
        WriteJson(Path.Combine(destination, "customer-profile.json"), new JsonObject { ["User"] = account }, indented: true);
        WriteJson(Path.Combine(destination, "Preferences"), new JsonObject { ["profile"] = new JsonObject { ["name"] = name } });
        Register(channel, folder, new JsonObject { ["name"] = name, ["is_using_default_name"] = false });
        if (!string.IsNullOrWhiteSpace(avatarPath)) SetAvatar(channel, folder, avatarPath);
        Log?.Invoke($"Profile '{name}' ({channel}/{folder}) created. Sign in to Edge as {account} to enable sync.");
        return GetProfile(channel, folder);
    }

    public void Rename(EdgeChannel channel, string profile, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("New name is missing.");
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        var preferencesFile = Path.Combine(p.Path, "Preferences");
        if (!File.Exists(preferencesFile)) throw new InvalidOperationException($"Profile '{profile}' has not been initialized by Edge yet.");
        var state = LoadLocalState(channel);
        if (state?["profile"]?["info_cache"]?[p.Folder] is not JsonObject info) throw new InvalidOperationException($"Profile '{profile}' is not registered.");

        var preferences = ReadJsonObject(preferencesFile);
        if (preferences["profile"] is not JsonObject profileNode) preferences["profile"] = profileNode = new JsonObject();
        profileNode["name"] = newName;
        info["name"] = newName;
        info["is_using_default_name"] = false;
        WriteJson(preferencesFile, preferences);
        SaveLocalState(channel, state);
        Log?.Invoke($"Profile '{p.Folder}' in Edge {channel} is now named '{newName}'.");
    }

    public void SetAvatar(EdgeChannel channel, string profile, string imagePath)
    {
        var image = ValidateAvatar(imagePath);
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        File.Copy(image, Path.Combine(p.Path, AvatarFileName), overwrite: true);
        var state = LoadLocalState(channel);
        if (state?["profile"]?["info_cache"]?[p.Folder] is not JsonObject info) throw new InvalidOperationException($"Profile '{profile}' is not registered in Local State.");
        info["gaia_picture_file_name"] = AvatarFileName;
        info["use_gaia_picture"] = true;
        info["is_using_default_avatar"] = false;
        info["last_downloaded_gaia_picture_url_with_size"] = "";
        SaveLocalState(channel, state);
        Log?.Invoke($"Custom avatar set for Edge {channel}/{p.Folder}.");
    }

    /// <summary>Theme IDs that Edge uses for a color theme without an extension.</summary>
    private static readonly string[] ColorThemeIds = ["autogenerated_theme_id", "dynamic_themepack"];

    /// <summary>
    /// Sets the profile color like "Appearance › Color" in Edge: color theme in the profile (Preferences)
    /// plus avatar and highlight color in Local State. Color as 0xRRGGBB; null removes the color
    /// again (default theme) while keeping a theme from an extension.
    /// </summary>
    public void SetColor(EdgeChannel channel, string profile, int? rgb)
    {
        var p = GetProfile(channel, profile);
        AssertClosed(channel);
        var preferencesFile = Path.Combine(p.Path, "Preferences");
        if (!File.Exists(preferencesFile)) throw new InvalidOperationException($"Profile '{profile}' has not been initialized by Edge yet.");
        var state = LoadLocalState(channel);
        if (state?["profile"]?["info_cache"]?[p.Folder] is not JsonObject info) throw new InvalidOperationException($"Profile '{profile}' is not registered.");

        var preferences = ReadJsonObject(preferencesFile);
        if (rgb is { } color)
        {
            // Chromium stores colors as signed ARGB (SkColor).
            var argb = unchecked((int)(0xFF000000u | ((uint)color & 0xFFFFFFu)));
            GetOrCreate(GetOrCreate(preferences, "extensions"), "theme")["id"] = "autogenerated_theme_id";
            GetOrCreate(GetOrCreate(preferences, "autogenerated"), "theme")["color"] = argb;
            info["profile_highlight_color"] = argb;
            info["default_avatar_fill_color"] = argb;
        }
        else
        {
            if (preferences["extensions"]?["theme"] is JsonObject theme && ColorThemeIds.Contains((string?)theme["id"])) theme.Remove("id");
            preferences.Remove("autogenerated");
            foreach (var key in (string[])["profile_highlight_color", "default_avatar_fill_color", "default_avatar_stroke_color", "profile_color_seed"])
                info.Remove(key);
        }
        WriteJson(preferencesFile, preferences);
        SaveLocalState(channel, state);
        Log?.Invoke(rgb is { } value ? $"Color #{value & 0xFFFFFF:X6} set for Edge {channel}/{p.Folder}." : $"Color removed for Edge {channel}/{p.Folder}.");
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
        Log?.Invoke($"Profile '{p.Name}' deleted from Edge {channel}.");
    }

    // ---------- Backup and restore ----------

    public string Backup(EdgeChannel channel, string profile, string destinationDir, TransferOptions options)
    {
        AssertClosed(channel, options.AllowRunningSource);
        var p = GetProfile(channel, profile);
        Log?.Invoke($"Backing up Edge {channel} – {p.Name} ({p.Folder}) …");
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
            Log?.Invoke($"Backup created: {zip}");
            Log?.Invoke("Note: Saved passwords/cookies are encrypted with DPAPI and cannot be read on another PC or by another user.");
            return zip;
        }
        finally { TryDeleteDirectory(temp); }
    }

    /// <summary>Restores a backup; without <paramref name="targetFolder"/> as a new profile. Returns the target folder.</summary>
    public string Restore(EdgeChannel channel, string zipPath, string? targetFolder, TransferOptions options)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException($"Backup file '{zipPath}' not found.");
        AssertClosed(channel);
        var userData = environment.UserDataDir(channel);
        if (!Directory.Exists(userData)) throw new InvalidOperationException($"Edge {channel} is not installed or has never been started.");

        var temp = Path.Combine(Path.GetTempPath(), "EdgeRs_" + Guid.NewGuid());
        try
        {
            Log?.Invoke($"Extracting backup: {zipPath}");
            ZipFile.ExtractToDirectory(zipPath, temp);
            var meta = ReadJsonObject(Path.Combine(temp, "meta.json"));
            var name = (string?)meta["Name"] ?? "Restored";
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
                Log?.Invoke($"Copying profile to Edge {channel}/{targetFolder} …");
                copier.Copy(source, destination, includeCaches: true);
                Register(channel, targetFolder, meta["InfoCache"]?.DeepClone());
                Log?.Invoke($"Profile '{name}' restored to Edge {channel}/{targetFolder}.");
            }
            return targetFolder;
        }
        finally { TryDeleteDirectory(temp); }
    }

    // ---------- Copy and move ----------

    /// <summary>Copies a profile to another channel. Returns the target folder.</summary>
    public string Copy(EdgeChannel from, EdgeChannel to, string profile, string? targetFolder, TransferOptions options)
    {
        if (from == to) throw new InvalidOperationException("Source and target are identical.");
        AssertClosed(from, options.AllowRunningSource);
        AssertClosed(to);
        var p = GetProfile(from, profile);
        var userData = environment.UserDataDir(to);
        if (!Directory.Exists(userData)) throw new InvalidOperationException($"Edge {to} is not installed or has never been started.");
        if (string.IsNullOrEmpty(targetFolder)) targetFolder = NextProfileFolder(to);
        var destination = Path.Combine(userData, targetFolder);

        if (options.LocalOnly)
        {
            var account = (string?)LoadLocalState(from)?["profile"]?["info_cache"]?[p.Folder]?["user_name"];
            ImportLocalData(to, p.Path, targetFolder, p.Name, account, options.IncludeCaches);
            return targetFolder;
        }

        ReplaceTarget(destination, targetFolder, options);
        Log?.Invoke($"Transferring {p.Name} from Edge {from} to {to}/{targetFolder} …");
        copier.Copy(p.Path, destination, options.IncludeCaches);
        Register(to, targetFolder, LoadLocalState(from)?["profile"]?["info_cache"]?[p.Folder]?.DeepClone());
        Log?.Invoke($"Profile '{p.Name}' copied from {from} to {to}/{targetFolder}.");
        return targetFolder;
    }

    /// <summary>
    /// Copies a profile to another channel, verifies every file via SHA256 and only then deletes
    /// the source. Returns the new target folder.
    /// </summary>
    public string MoveVerified(EdgeChannel from, EdgeChannel to, string profile, TransferOptions options)
    {
        if (from == to) throw new InvalidOperationException("Source and target are identical.");
        if (options.LocalOnly || options.AllowRunningSource)
            throw new InvalidOperationException("Moving requires a complete profile and closed Edge instances.");
        AssertClosed(from);
        AssertClosed(to);
        var source = GetProfile(from, profile);
        if (LoadLocalState(to) is null) throw new InvalidOperationException($"Start Edge {to} once first.");
        if (Directory.EnumerateFileSystemEntries(source.Path, "*", SearchOption.AllDirectories)
            .Any(entry => File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidOperationException("For safety reasons, profiles containing symbolic links or directory junctions are not moved.");

        var folder = NextProfileFolder(to);
        var destination = Path.Combine(environment.UserDataDir(to), folder);
        Log?.Invoke("Computing source checksums …");
        var manifest = GetRelevantFiles(source.Path, options.IncludeCaches).ToDictionary(r => r, r => Hash(Path.Combine(source.Path, r)), StringComparer.OrdinalIgnoreCase);

        Copy(from, to, source.Folder, folder, options with { ReplaceExisting = false });
        if (LoadLocalState(to)?["profile"]?["info_cache"]?[folder] is null) throw new InvalidOperationException("Target profile not registered. Source is kept.");
        AssertClosed(from);
        AssertClosed(to);

        Log?.Invoke("Verifying target copy …");
        if (GetRelevantFiles(source.Path, options.IncludeCaches).Count() != manifest.Count)
            throw new InvalidOperationException("Source files have changed. Source is kept.");
        foreach (var (relative, hash) in manifest)
        {
            var target = Path.Combine(destination, relative);
            if (!File.Exists(target) || Hash(target) != hash || Hash(Path.Combine(source.Path, relative)) != hash)
                throw new InvalidOperationException($"Verification failed: {relative}. Source is kept.");
        }
        if (guard.IsRunning(from) || guard.IsRunning(to)) throw new InvalidOperationException("Edge was started during verification. Source is kept.");

        Delete(from, source.Folder);
        Log?.Invoke($"Profile moved to {to}/{folder}; target copy verified with SHA256.");
        return folder;
    }

    // ---------- Helpers ----------

    private void AssertClosed(EdgeChannel channel, bool allowRunning = false)
    {
        if (!guard.IsRunning(channel)) return;
        if (!allowRunning) throw new EdgeRunningException(channel);
        Log?.Invoke($"Warning: Edge {channel} is still running. The profile copy may be incomplete or inconsistent.");
    }

    private void ReplaceTarget(string destination, string targetFolder, TransferOptions options)
    {
        if (!Directory.Exists(destination)) return;
        if (!options.ReplaceExisting) throw new InvalidOperationException($"Target profile '{targetFolder}' already exists. Confirm to replace it.");
        Directory.Delete(destination, recursive: true);
    }

    /// <summary>Shared part of restore/copy with "local data only" (new or existing target profile).</summary>
    private void ImportLocalData(EdgeChannel channel, string source, string targetFolder, string name, string? account, bool includeCaches)
    {
        var destination = Path.Combine(environment.UserDataDir(channel), targetFolder);
        var isNew = !Directory.Exists(destination);
        if (!isNew && !File.Exists(Path.Combine(destination, "Preferences")))
            throw new InvalidOperationException($"Target folder '{targetFolder}' is not an existing Edge profile.");
        if (isNew && LoadLocalState(channel) is null)
            throw new InvalidOperationException($"No 'Local State' file found in {channel}. Start Edge once.");

        Log?.Invoke($"Transferring local data to Edge {channel}/{targetFolder} …");
        Directory.CreateDirectory(destination);
        CopyLocalData(source, destination, includeCaches);
        if (isNew)
        {
            Register(channel, targetFolder, new JsonObject { ["name"] = name });
            if (!string.IsNullOrEmpty(account)) Log?.Invoke($"Account to sign in again: {account}");
            Log?.Invoke($"New profile created without sign-in. Start Edge {channel}/{targetFolder} and sign in to enable sync.");
        }
        Log?.Invoke($"Local data transferred to Edge {channel}/{targetFolder}.");
    }

    private void CopyLocalData(string source, string destination, bool includeCaches)
    {
        foreach (var directory in LocalDataDirs)
        {
            var from = Path.Combine(source, directory);
            if (!Directory.Exists(from)) continue;
            Log?.Invoke($"  Copying {directory} …");
            copier.Copy(from, Path.Combine(destination, directory), includeCaches);
        }
    }

    private void Register(EdgeChannel channel, string folder, JsonNode? infoCache)
    {
        var state = LoadLocalState(channel);
        if (state is null)
        {
            Log?.Invoke($"Warning: No 'Local State' file found in {channel} – start Edge once.");
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
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) throw new FileNotFoundException($"Avatar file '{imagePath}' not found.");
        if (!string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The profile avatar must be a PNG file.");
        var signature = new byte[8];
        using (var stream = File.OpenRead(imagePath))
        {
            if (stream.ReadAtLeast(signature, 8, throwOnEndOfStream: false) != 8 || !signature.SequenceEqual(PngSignature))
                throw new ArgumentException("The avatar file is not a valid PNG.");
        }
        return Path.GetFullPath(imagePath);
    }

    private static JsonObject ReadJsonObject(string file) =>
        JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? throw new InvalidDataException($"{file} does not contain a JSON object.");

    private static void WriteJson(string file, JsonNode node, bool indented = false) =>
        File.WriteAllText(file, indented ? node.ToJsonString(IndentedJson) : node.ToJsonString(), new UTF8Encoding(false));

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
