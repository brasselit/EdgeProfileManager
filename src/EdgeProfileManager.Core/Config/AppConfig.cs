using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeProfileManager.Core.Config;

public sealed class AppConfig
{
    public string BackupPath { get; set; } = @"%USERPROFILE%\EdgeProfileBackups";

    /// <summary>Behavior when a profile has no desktopMode of its own.</summary>
    public DesktopMode DefaultDesktopMode { get; set; } = DesktopMode.SwitchTo;

    /// <summary>Show profiles that are not in the menu tree under "Unassigned".</summary>
    public bool ShowUnassigned { get; set; } = true;

    /// <summary>Also show unassigned profiles in the Jump List.</summary>
    public bool JumpListUnassigned { get; set; }

    public JumpListStyle JumpListStyle { get; set; } = JumpListStyle.Categories;

    /// <summary>Check the GitHub releases for a new version at start and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Program for "Edit configuration"; empty = file association, VS Code or Notepad.</summary>
    public string? Editor { get; set; }

    public List<MenuEntry> Menu { get; set; } = [];
}

public enum JumpListStyle
{
    /// <summary>One Jump List category per group. Windows shows only a limited number of entries there
    /// and hides them when "Show recently opened items in Jump Lists" is turned off.</summary>
    Categories,
    /// <summary>All profiles in the "Tasks" section, without grouping but also without these restrictions.</summary>
    Tasks,
}

/// <summary>An entry in the menu tree: group, profile or separator.</summary>
public sealed class MenuEntry
{
    public string? Group { get; set; }
    public List<MenuEntry>? Children { get; set; }

    /// <summary>Profile reference "Channel/Folder", e.g. "Stable/Profile 1".</summary>
    public string? Profile { get; set; }
    public string? Label { get; set; }
    public DesktopTarget? Desktop { get; set; }
    public DesktopMode? DesktopMode { get; set; }
    public bool? JumpList { get; set; }

    public bool? Separator { get; set; }
}

public enum DesktopMode
{
    /// <summary>Switch to the desktop the window is on.</summary>
    SwitchTo,
    /// <summary>Bring the window to the current desktop.</summary>
    BringHere,
    /// <summary>Move the window to the assigned desktop and switch there.</summary>
    MoveToAssigned,
}

/// <summary>Virtual desktop by number (1-based) or name.</summary>
[JsonConverter(typeof(DesktopTargetConverter))]
public sealed record DesktopTarget(int? Number, string? Name)
{
    public override string ToString() => Number?.ToString() ?? Name ?? string.Empty;
}

internal sealed class DesktopTargetConverter : JsonConverter<DesktopTarget>
{
    public override DesktopTarget? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => new DesktopTarget(reader.GetInt32(), null),
        JsonTokenType.String when int.TryParse(reader.GetString(), out var n) => new DesktopTarget(n, null),
        JsonTokenType.String => new DesktopTarget(null, reader.GetString()),
        JsonTokenType.Null => null,
        _ => throw new JsonException("desktop must be a number or a name."),
    };

    public override void Write(Utf8JsonWriter writer, DesktopTarget value, JsonSerializerOptions options)
    {
        if (value.Number is { } number) writer.WriteNumberValue(number);
        else writer.WriteStringValue(value.Name);
    }
}
