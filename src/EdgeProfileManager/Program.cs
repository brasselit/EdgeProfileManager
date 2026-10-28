using System.Runtime.InteropServices;
using EdgeProfileManager.Core.Edge;

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
        RemoveInheritedVsCodeEnvironment();
        var command = CommandLine.Parse(args);
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
            // Dieser Prozess wurde vom Benutzer gestartet und darf den Vordergrund vergeben;
            // die Tray-Instanz braucht das Recht, um Edge-Fenster bzw. das Menü nach vorne zu holen.
            AllowSetForegroundWindow(ASFW_ANY);
            return SingleInstance.TrySend(args.Length == 0 ? ["--menu"] : args) ? 0 : 1;
        }
        if (command.Kind == CommandKind.Exit) return 0;

        return new App(command, instance).Run();
    }

    /// <summary>
    /// Aus VS Code gestartet (z.B. beim Debuggen) erbt der Prozess ELECTRON_RUN_AS_NODE=1 und VSCODE_*.
    /// Damit würde ein gestartetes Code.exe als reines Node.js ohne Fenster laufen und sofort enden.
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
            MessageBox.Show(text, "Edge-Fenster");
        }
        return 0;
    }
}
