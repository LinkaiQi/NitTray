using System.Globalization;
using System.IO;
using System.Text;

namespace NitTray.Services;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = ResolveLogPath();

    public static string FilePath => LogPath;

    public static string FolderPath => Path.GetDirectoryName(LogPath) ?? AppContext.BaseDirectory;

    public static void Reset(string reason)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(LogPath,
                $"=== NitTray diagnostic log ===\n" +
                $"Started: {DateTime.Now:O}\n" +
                $"Reason: {reason}\n\n",
                Encoding.UTF8);
        }
        catch
        {
            // Best-effort. If we can't write the log, the app should still work.
        }
    }

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.AppendAllText(
                    LogPath,
                    string.Concat(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        " ", message, Environment.NewLine),
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never crash the app.
            }
        }
    }

    private static string ResolveLogPath()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);

            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = AppContext.BaseDirectory;
            }

            return Path.Combine(baseDir, "NitTray", "diagnostic.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "diagnostic.log");
        }
    }
}
