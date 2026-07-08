using System.Globalization;
using System.IO;
using System.Text;

namespace NitTray.Services;

// Verbose display-enumeration log (%LOCALAPPDATA%\NitTray\diagnostic.log), written
// in every build for bug reports. Only enumeration and error paths write, so it
// stays off the brightness hot path. WriteCritical marks fatal events.
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

    public static void Write(string message) => Append(message);

    public static void WriteCritical(string message) => Append(message);

    private static void Append(string message)
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
