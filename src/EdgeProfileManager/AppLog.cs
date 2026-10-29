namespace EdgeProfileManager;

/// <summary>Simple file log at %LOCALAPPDATA%\EdgeProfileManager\log.txt.</summary>
public static class AppLog
{
    private const long MaxSize = 1024 * 1024;
    private static readonly Lock Sync = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeProfileManager");

    public static string FilePath { get; } = Path.Combine(Directory, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxSize)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never disrupt the application.
        }
    }
}
