using System.Diagnostics;
using System.Globalization;
using EdgeProfileManager.Core.Edge;
using EdgeProfileManager.Core.Interop;
using EdgeProfileManager.Core.Resources;

namespace EdgeProfileManager.Core.Management;

/// <summary>Checks for and terminates running Edge processes of a channel.</summary>
public interface IEdgeProcessGuard
{
    bool IsRunning(EdgeChannel channel);

    /// <summary>Closes all windows of the channel and terminates remaining processes.</summary>
    void Close(EdgeChannel channel);
}

public sealed class EdgeRunningException(EdgeChannel channel)
    : InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Error_EdgeRunning, channel))
{
    public EdgeChannel Channel { get; } = channel;
}

/// <summary>Detects running msedge.exe processes per channel and closes them, gracefully first, then forcibly.</summary>
public sealed class EdgeProcessGuard(EdgeWindowFinder finder) : IEdgeProcessGuard
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(8);

    public bool IsRunning(EdgeChannel channel) => GetProcesses(channel).Any();

    public void Close(EdgeChannel channel)
    {
        // Close normally first so Edge saves its session and settings cleanly.
        foreach (var window in finder.GetWindows().Where(w => w.Channel == channel))
        {
            Native.PostMessage(window.Handle, Native.WM_CLOSE, 0, 0);
        }
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < GracefulTimeout && IsRunning(channel)) Thread.Sleep(250);

        // Background processes (startup boost, extensions) often remain.
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
