using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace NitTray.Services;

// Verbose diagnostic logging. In Release builds the [Conditional("DEBUG")] calls
// (Reset/Write) are stripped by the compiler — including evaluation of their
// arguments — so the enumeration/brightness hot paths do no logging work at all.
// Critical failures still go to the log in every configuration via WriteCritical.
internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = ResolveLogPath();

    public static string FilePath => LogPath;

    public static string FolderPath => Path.GetDirectoryName(LogPath) ?? AppContext.BaseDirectory;

    [Conditional("DEBUG")]
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

    [Conditional("DEBUG")]
    public static void Write(string message) => Append(message);

    // Records a message in every build configuration (Debug and Release). Reserved
    // for genuinely important events — currently unhandled/fatal errors — so a
    // Release build still leaves a breadcrumb when something goes badly wrong.
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
