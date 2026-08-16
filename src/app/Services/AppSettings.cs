using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitTray.Services;

// User preferences persisted next to the diagnostic log
// (%LOCALAPPDATA%\NitTray\settings.json). Shortcuts are stored as text so the file
// stays hand-editable and survives changes to the Key enum.
public sealed class AppSettings
{
    public bool BrightnessHotKeysEnabled { get; set; }

    public string BrightnessUpHotKey { get; set; } =
        HotKeyBinding.DefaultBrightnessUp.ToStorageString();

    public string BrightnessDownHotKey { get; set; } =
        HotKeyBinding.DefaultBrightnessDown.ToStorageString();

    // Unparseable text falls back to the default rather than silently disabling the
    // shortcut; the settings window then shows what is actually registered.
    [JsonIgnore]
    public HotKeyBinding BrightnessUp =>
        HotKeyBinding.Parse(BrightnessUpHotKey) ?? HotKeyBinding.DefaultBrightnessUp;

    [JsonIgnore]
    public HotKeyBinding BrightnessDown =>
        HotKeyBinding.Parse(BrightnessDownHotKey) ?? HotKeyBinding.DefaultBrightnessDown;
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath { get; } =
        Path.Combine(DiagnosticLog.FolderPath, "settings.json");

    // Never throws: a missing or corrupt file just yields defaults.
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Settings: falling back to defaults ({ex.Message}).");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Directory.CreateDirectory(DiagnosticLog.FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Settings: could not be saved ({ex.Message}).");
        }
    }
}
