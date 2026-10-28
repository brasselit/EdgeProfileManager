using System.Diagnostics;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Core.Interop;

namespace EdgeProfileManager.Core.Management;

/// <summary>Prüft und beendet laufende Edge-Instanzen eines Channels.</summary>
public interface IEdgeProcessGuard
{
    bool IsRunning(EdgeChannel channel);

    /// <summary>Schliesst alle Fenster des Channels und beendet verbleibende Prozesse.</summary>
    void Close(EdgeChannel channel);
}

public sealed class EdgeRunningException(EdgeChannel channel)
    : InvalidOperationException($"Edge {channel} läuft noch. Bitte schliessen oder beenden lassen.")
{
    public EdgeChannel Channel { get; } = channel;
}

/// <summary>Port von Test-EdgeRunning / Assert-EdgeClosed -Force.</summary>
public sealed class EdgeProcessGuard(EdgeWindowFinder finder) : IEdgeProcessGuard
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(8);

    public bool IsRunning(EdgeChannel channel) => GetProcesses(channel).Any();

    public void Close(EdgeChannel channel)
    {
        // Zuerst regulär schliessen, damit Edge Sitzung und Einstellungen sauber speichert.
        foreach (var window in finder.GetWindows().Where(w => w.Channel == channel))
        {
            Native.PostMessage(window.Handle, Native.WM_CLOSE, 0, 0);
        }
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < GracefulTimeout && IsRunning(channel)) Thread.Sleep(250);

        // Hintergrundprozesse (Startup Boost, Erweiterungen) bleiben oft bestehen.
        foreach (var process in GetProcesses(channel))
        {
            try { process.Kill(); process.WaitForExit(5000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        Thread.Sleep(1000);
        if (IsRunning(channel)) throw new EdgeRunningException(channel);
    }

    private static IEnumerable<Process> GetProcesses(EdgeChannel channel)
    {
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            if (EdgeChannels.FromExecutablePath(Native.GetProcessPath((uint)process.Id)) == channel) yield return process;
            else process.Dispose();
        }
    }
}
