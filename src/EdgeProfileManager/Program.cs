using System.Runtime.InteropServices;
using System.Windows.Forms;
using EdgeProfileManager.Core.Config;
using EdgeProfileManager.Core.Edge;
using Velopack;

namespace EdgeProfileManager;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    private const int ASFW_ANY = -1;
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack runs its install/update/uninstall hooks here and exits the process afterwards.
        var firstRun = false;
        VelopackApp.Build()
            .SetAppUserModelId(ShellIntegration.AppUserModelId)
            .OnBeforeUninstallFastCallback(_ => ShellIntegration.RemoveIntegration())
            .OnFirstRun(_ => firstRun = true)
            .Run();
        ShellIntegration.SetProcessAppUserModelId();
        RemoveInheritedVsCodeEnvironment();
        Localization.Apply(ConfigStore.ReadLanguage(ConfigStore.DefaultPath));

        // The setup starts the app right after installing: only go to the tray instead of showing the menu.
        var command = firstRun ? new CommandLine(CommandKind.Background) : CommandLine.Parse(args);
        switch (command.Kind)
        {
            case CommandKind.Invalid:
                MessageBox.Show($"{command.Error}\n\n{CommandLine.Usage}", "Edge Profile Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            case CommandKind.DumpWindows:
                return DumpWindows();
        }

        using var instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            // This process was started by the user and may grant the foreground right;
            // the tray instance needs it to bring Edge windows or the menu to the front.
            AllowSetForegroundWindow(ASFW_ANY);
            return SingleInstance.TrySend(args.Length == 0 ? ["--menu"] : args) ? 0 : 1;
        }
        if (command.Kind == CommandKind.Exit) return 0;

        return new App(command, instance, firstRun).Run();
    }

    /// <summary>
    /// When started from VS Code (e.g. while debugging), the process inherits ELECTRON_RUN_AS_NODE=1 and VSCODE_*.
    /// A launched Code.exe would then run as plain Node.js without a window and exit immediately.
    /// </summary>
    private static void RemoveInheritedVsCodeEnvironment()
    {
        foreach (var name in System.Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            if (name.Equals("ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase))
            {
                System.Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static int DumpWindows()
    {
        var text = ShellIntegration.DescribeWindows(new ProfileDiscovery(EdgeEnvironment.Default), new EdgeWindowFinder());
        if (AttachConsole(ATTACH_PARENT_PROCESS))
        {
            Console.WriteLine();
            Console.Write(text);
        }
        else
        {
            MessageBox.Show(text, "Edge windows");
        }
        return 0;
    }
}
