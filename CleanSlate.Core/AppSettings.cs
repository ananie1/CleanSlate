using System.Text.Json;

namespace CleanSlate.Core;

public sealed class AppSettings
{
    public bool ScanProgramData { get; set; } = true;
    public bool ScanInstallDir { get; set; } = true;
    public bool QuietUninstall { get; set; } = false;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSlate", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { AppLog.Instance.Warn($"Не удалось прочитать настройки: {ex.Message}"); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Instance.Warn($"Не удалось сохранить настройки: {ex.Message}"); }
    }
}
