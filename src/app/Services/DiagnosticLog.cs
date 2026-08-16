using System.Globalization;
using System.IO;
using System.Text;

namespace NitTray.Services;

// Verbose display-enumeration log (%LOCALAPPDATA%\NitTray\diagnostic.log), written
// in every build for bug reports. Only enumeration and error paths write, so it
// stays off the brightness hot path. WriteCritical marks fatal events.
//
// Each scan starts the file over; MaxLogBytes bounds the writes in between, so a
// session that runs a long time without rescanning can't grow it without limit.
internal static class DiagnosticLog
{
    // Far more than one enumeration produces, so it never cuts a scan in half.
    private const long MaxLogBytes = 1_000_000;

    private static readonly object Sync = new();
    private static readonly string LogPath = ResolveLogPath();

    public static string FilePath => LogPath;

    public static string FolderPath => Path.GetDirectoryName(LogPath) ?? AppContext.BaseDirectory;

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            Restart(reason);
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

                var current = new FileInfo(LogPath);
                if (current.Exists && current.Length >= MaxLogBytes)
                {
                    Restart("size limit reached");
                }

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

    // Caller must hold Sync.
    private static void Restart(string reason)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(LogPath,
                "=== NitTray diagnostic log ===" + Environment.NewLine +
                $"Started: {DateTime.Now:O}" + Environment.NewLine +
                $"Reason: {reason}" + Environment.NewLine + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // Best-effort. If we can't write the log, the app should still work.
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
