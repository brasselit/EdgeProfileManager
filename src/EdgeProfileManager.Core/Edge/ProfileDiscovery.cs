using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EdgeProfileManager.Core.Edge;

public sealed record EdgeProfile(EdgeChannel Channel, string Folder, string Name, string? User, string Path, string? IconPath)
{
    /// <summary>Stabile Referenz "Channel/Folder", wie sie in der Konfiguration verwendet wird.</summary>
    public string Key => $"{Channel}/{Folder}";
}

/// <summary>Port von Get-EdgeProfiles / Get-LocalState aus EdgeProfile.ps1.</summary>
public sealed partial class ProfileDiscovery(EdgeEnvironment environment)
{
    public EdgeEnvironment Environment { get; } = environment;

    public IReadOnlyList<EdgeProfile> GetProfiles() => EdgeChannels.All.SelectMany(GetProfiles).ToList();

    public IReadOnlyList<EdgeProfile> GetProfiles(EdgeChannel channel)
    {
        var userData = Environment.UserDataDir(channel);
        if (!Directory.Exists(userData)) return [];
        var infoCache = ReadLocalState(userData)?["profile"]?["info_cache"] as JsonObject;

        var result = new List<EdgeProfile>();
        foreach (var dir in Directory.EnumerateDirectories(userData))
        {
            var folder = System.IO.Path.GetFileName(dir);
            if (folder != "Default" && !folder.StartsWith("Profile ", StringComparison.Ordinal)) continue;
            var info = infoCache?[folder];
            if (info is null && !File.Exists(System.IO.Path.Combine(dir, "Preferences"))) continue;

            var name = (string?)info?["name"] ?? folder;
            var user = ReadCustomerUser(dir) ?? NullIfEmpty((string?)info?["user_name"]);
            var icon = System.IO.Path.Combine(dir, "Edge Profile.ico");
            result.Add(new EdgeProfile(channel, folder, name, user, dir, File.Exists(icon) ? icon : null));
        }
        result.Sort((a, b) => FolderSortKey(a.Folder).CompareTo(FolderSortKey(b.Folder)));
        return result;
    }

    public static JsonNode? ReadLocalState(string userDataDir)
    {
        var file = System.IO.Path.Combine(userDataDir, "Local State");
        if (!File.Exists(file)) return null;
        try
        {
            // Edge hält die Datei offen; deshalb mit FileShare.ReadWrite lesen.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadCustomerUser(string profileDir)
    {
        var file = System.IO.Path.Combine(profileDir, "customer-profile.json");
        if (!File.Exists(file)) return null;
        try { return NullIfEmpty((string?)JsonNode.Parse(File.ReadAllText(file))?["User"]); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Default zuerst, danach "Profile N" numerisch sortiert.</summary>
    private static int FolderSortKey(string folder)
    {
        if (folder == "Default") return 0;
        var match = ProfileNumber().Match(folder);
        return match.Success ? int.Parse(match.Groups[1].Value) : int.MaxValue;
    }

    [GeneratedRegex(@"^Profile (\d+)$")]
    private static partial Regex ProfileNumber();
}
