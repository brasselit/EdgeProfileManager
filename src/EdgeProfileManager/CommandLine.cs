using System.Globalization;
using EdgeProfileManager.Resources;

namespace EdgeProfileManager;

public enum CommandKind { ShowMenu, Background, Open, Manage, DumpWindows, Reload, Exit, Invalid }

/// <summary>
/// Command line. Without arguments (e.g. a click on the pinned taskbar button) the profile menu
/// is shown at the mouse position.
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
                    if (i + 1 >= args.Count) return Invalid(Strings.CommandLine_OpenExpectsProfile);
                    kind = CommandKind.Open;
                    profile = args[++i];
                    break;
                case "--new-window": newWindow = true; break;
                case "--background": kind = CommandKind.Background; break;
                case "--menu": kind = CommandKind.ShowMenu; break;
                case "--manage": kind = CommandKind.Manage; break;
                case "--dump-windows": kind = CommandKind.DumpWindows; break;
                case "--reload": kind = CommandKind.Reload; break;
                case "--exit": kind = CommandKind.Exit; break;
                default: return Invalid(string.Format(CultureInfo.CurrentCulture, Strings.CommandLine_UnknownArgument, args[i]));
            }
        }
        return new(kind, profile, newWindow);
    }

    private static CommandLine Invalid(string error) => new(CommandKind.Invalid, Error: error);

    public static string Usage => Strings.CommandLine_Usage;
}
