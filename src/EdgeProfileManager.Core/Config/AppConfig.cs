using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeProfileManager.Core.Config;

public sealed class AppConfig
{
    public string BackupPath { get; set; } = @"%USERPROFILE%\EdgeProfileBackups";

    /// <summary>Verhalten, wenn ein Profil keine eigene desktopMode-Angabe hat.</summary>
    public DesktopMode DefaultDesktopMode { get; set; } = DesktopMode.SwitchTo;

    /// <summary>Profile, die nicht im Menübaum stehen, in "Nicht zugeordnet" anzeigen.</summary>
    public bool ShowUnassigned { get; set; } = true;

    /// <summary>Nicht zugeordnete Profile auch in der Jump List anzeigen.</summary>
    public bool JumpListUnassigned { get; set; }

    public JumpListStyle JumpListStyle { get; set; } = JumpListStyle.Categories;

    /// <summary>Programm für "Konfiguration bearbeiten"; leer = Dateizuordnung, VS Code oder Notepad.</summary>
    public string? Editor { get; set; }

    public List<MenuEntry> Menu { get; set; } = [];
}

public enum JumpListStyle
{
    /// <summary>Eine Jump-List-Kategorie pro Gruppe. Windows zeigt dort nur begrenzt viele Einträge
    /// und blendet sie aus, wenn "Zuletzt geöffnete Elemente in Sprunglisten" deaktiviert ist.</summary>
    Categories,
    /// <summary>Alle Profile im Abschnitt "Aufgaben", ohne Gruppierung, aber ohne diese Einschränkungen.</summary>
    Tasks,
}

/// <summary>Ein Eintrag im Menübaum: Gruppe, Profil oder Trennlinie.</summary>
public sealed class MenuEntry
{
    public string? Group { get; set; }
    public List<MenuEntry>? Children { get; set; }

    /// <summary>Profilreferenz "Channel/Folder", z.B. "Stable/Profile 1".</summary>
    public string? Profile { get; set; }
    public string? Label { get; set; }
    public DesktopTarget? Desktop { get; set; }
    public DesktopMode? DesktopMode { get; set; }
    public bool? JumpList { get; set; }

    public bool? Separator { get; set; }
}

public enum DesktopMode
{
    /// <summary>Zum Desktop wechseln, auf dem das Fenster liegt.</summary>
    SwitchTo,
    /// <summary>Fenster auf den aktuellen Desktop holen.</summary>
    BringHere,
    /// <summary>Fenster auf den zugewiesenen Desktop verschieben und dorthin wechseln.</summary>
    MoveToAssigned,
}

/// <summary>Virtueller Desktop per Nummer (1-basiert) oder Name.</summary>
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
        _ => throw new JsonException("desktop muss eine Nummer oder ein Name sein."),
    };

    public override void Write(Utf8JsonWriter writer, DesktopTarget value, JsonSerializerOptions options)
    {
        if (value.Number is { } number) writer.WriteNumberValue(number);
        else writer.WriteStringValue(value.Name);
    }
}
