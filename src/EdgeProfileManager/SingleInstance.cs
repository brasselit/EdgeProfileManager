using System.IO.Pipes;
using System.Text.Json;

namespace EdgeProfileManager;

/// <summary>
/// Stellt sicher, dass nur eine Tray-Instanz läuft. Weitere Aufrufe (Jump List, Taskbar-Klick)
/// leiten ihre Argumente über eine Named Pipe an diese Instanz weiter.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = $"EdgeProfileManager-{Environment.UserName}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancel = new();

    public SingleInstance()
    {
        _mutex = new Mutex(true, $@"Local\{Id}", out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    // Pipes sind sitzungsübergreifend sichtbar; die Sitzungs-ID entspricht dem Local\-Mutex.
    private static readonly string PipeName = $"{Id}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    public static bool TrySend(IReadOnlyList<string> args)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.Write(JsonSerializer.Serialize(args));
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // Primäre Instanz startet evtl. gerade noch.
                Thread.Sleep(300);
            }
        }
        return false;
    }

    public void StartServer(Action<string[]> onCommand)
    {
        var token = _cancel.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var args = JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync(token)) ?? [];
                    onCommand(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { AppLog.Write($"Pipe-Fehler: {ex.Message}"); }
            }
        }, token);
    }

    public void Dispose()
    {
        _cancel.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
