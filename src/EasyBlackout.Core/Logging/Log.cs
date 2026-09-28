using System.Text;

namespace EasyBlackout.Core.Logging;

/// <summary>Minimal thread-safe rolling file logger (one file per day, 7 days retained).</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static string? _directory;

    public static string Directory => _directory ??= InitDirectory(DefaultDirectory);

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyBlackout", "logs");

    public static void Initialize(string? directory = null)
    {
        _directory = InitDirectory(directory ?? DefaultDirectory);
        PruneOldFiles();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static string InitDirectory(string dir)
    {
        try { System.IO.Directory.CreateDirectory(dir); } catch { /* logging must never throw */ }
        return dir;
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Sync)
        {
            try
            {
                File.AppendAllText(Path.Combine(Directory, $"EasyBlackout-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
            catch
            {
                // Disk full / permissions — never let logging take the app down.
            }
        }
    }

    private static void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "EasyBlackout-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch { /* best effort */ }
    }
}
