using System.Globalization;
using System.IO;
using System.Text;

namespace NitTray.Services;

// Verbose diagnostic logging, written in every build configuration. It captures
// the display-enumeration trace — every HID/USB device seen and why each was or
// wasn't chosen as the brightness interface — so a user whose display isn't
// detected can attach the log (%LOCALAPPDATA%\NitTray\diagnostic.log) to a bug
// report. Logging stays off the brightness hot path: only enumeration and error
// paths write, so normal slider use doesn't churn the file. WriteCritical is
// reserved for fatal errors and is identical to Write today, kept as a distinct
// name to mark genuinely important events.
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

    // Records a message in every build configuration, same as Write today. Kept as
    // a distinct name to mark genuinely important events (currently unhandled/fatal
    // errors) so their intent is clear at the call site.
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
