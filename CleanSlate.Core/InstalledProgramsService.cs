using Microsoft.Win32;

namespace CleanSlate.Core;

/// <summary>Читает зарегистрированные программы из ключей Uninstall (HKLM 64/32 бит и HKCU).</summary>
public static class InstalledProgramsService
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    static readonly (RegistryHive Hive, RegistryView View)[] Sources =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser,  RegistryView.Registry64),
    };

    public static List<InstalledProgram> Load(ILogSink? log = null)
    {
        log ??= NullLog.Instance;
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in Sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallKey);
                if (root == null) continue;

                foreach (var keyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(keyName);
                        if (k == null) continue;
                        var p = Read(k, keyName, hive, view);
                        if (p != null && seen.Add($"{p.Name}|{p.Version}|{p.Publisher}")) result.Add(p);
                    }
                    catch (Exception ex) { log.Warn($"Пропущена запись реестра {keyName}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { log.Warn($"Не удалось прочитать {hive}/{view}: {ex.Message}"); }
        }

        log.Info($"Найдено зарегистрированных программ: {result.Count}");
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string Str(RegistryKey k, string name) => (k.GetValue(name) as string)?.Trim() ?? "";

    static InstalledProgram? Read(RegistryKey k, string keyName, RegistryHive hive, RegistryView view)
    {
        var name = Str(k, "DisplayName");
        if (name.Length == 0) return null;
        if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;          // скрытые системные компоненты
        if (Str(k, "ParentKeyName").Length > 0) return null;                            // обновления
        if (Str(k, "ReleaseType") is "Update" or "Hotfix" or "Security Update" or "Update Rollup" or "Service Pack") return null;

        var unin = Str(k, "UninstallString");
        if (unin.Length == 0 && k.GetValue("WindowsInstaller") is int wi && wi == 1 && keyName.StartsWith('{'))
            unin = $"MsiExec.exe /X{keyName}";

        return new InstalledProgram
        {
            Name = name,
            Publisher = Str(k, "Publisher"),
            Version = Str(k, "DisplayVersion"),
            InstallLocation = Str(k, "InstallLocation").Trim('"'),
            UninstallString = unin,
            QuietUninstallString = Str(k, "QuietUninstallString"),
            Hive = hive, View = view,
            SubKey = $@"{UninstallKey}\{keyName}",
        };
    }
}
