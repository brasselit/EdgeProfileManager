namespace EdgeProfileManager;

public enum CommandKind { ShowMenu, Background, Open, DumpWindows, Reload, Exit, Invalid }

/// <summary>
/// Befehlszeile. Ohne Argumente (z.B. Klick auf die angeheftete Taskbar-Schaltfläche) wird das
/// Profilmenü an der Mausposition angezeigt.
/// </summary>
public sealed record CommandLine(CommandKind Kind, string? ProfileKey = null, bool NewWindow = false, string? Error = null)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new(CommandKind.ShowMenu);
        string? profile = null;
        var newWindow = false;
        var kind = CommandKind.ShowMenu;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--open":
                    if (i + 1 >= args.Count) return Invalid("--open erwartet eine Profilreferenz wie \"Stable/Profile 1\".");
                    kind = CommandKind.Open;
                    profile = args[++i];
                    break;
                case "--new-window": newWindow = true; break;
                case "--background": kind = CommandKind.Background; break;
                case "--menu": kind = CommandKind.ShowMenu; break;
                case "--dump-windows": kind = CommandKind.DumpWindows; break;
                case "--reload": kind = CommandKind.Reload; break;
                case "--exit": kind = CommandKind.Exit; break;
                default: return Invalid($"Unbekanntes Argument: {args[i]}");
            }
        }
        return new(kind, profile, newWindow);
    }

    private static CommandLine Invalid(string error) => new(CommandKind.Invalid, Error: error);

    public static string Usage =>
        """
        EdgeProfileManager.exe                     Profilmenü anzeigen (startet bei Bedarf im Tray)
        EdgeProfileManager.exe --background        Nur im Tray starten (Autostart)
        EdgeProfileManager.exe --open "Stable/Profile 1" [--new-window]
        EdgeProfileManager.exe --dump-windows      Edge-Fenster mit Profil und AUMID auflisten
        EdgeProfileManager.exe --reload | --exit
        """;
}
