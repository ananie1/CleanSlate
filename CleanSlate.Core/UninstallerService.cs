using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CleanSlate.Core;

public sealed record UninstallOutcome(bool Started, int? ExitCode, bool StillRegistered, string Message);

/// <summary>Запуск штатного деинсталлятора. Права администратора сам не запрашивает: UAC покажет сам деинсталлятор, если ему нужно.</summary>
public static class UninstallerService
{
    public static string GetCommand(InstalledProgram p, bool preferQuiet)
    {
        var cmd = preferQuiet && p.QuietUninstallString.Length > 0 ? p.QuietUninstallString : p.UninstallString;
        // Некоторые MSI-записи содержат /I (установка/ремонт) вместо /X (удаление)
        return Regex.Replace(cmd, @"(?i)(msiexec(\.exe)?\s+)/I(\{)", "$1/X$3");
    }

    public static bool TryParse(string command, out string file, out string args)
    {
        file = ""; args = "";
        var c = (command ?? "").Trim();
        if (c.Length == 0) return false;

        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            if (end < 0) return false;
            file = c[1..end]; args = c[(end + 1)..].Trim();
            return file.Length > 0;
        }
        if (File.Exists(c)) { file = c; return true; }

        int idx = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) { file = c[..(idx + 4)]; args = c[(idx + 4)..].Trim(); return true; }

        int sp = c.IndexOf(' ');
        if (sp < 0) { file = c; return true; }
        file = c[..sp]; args = c[(sp + 1)..].Trim();
        return true;
    }

    public static async Task<UninstallOutcome> RunAsync(InstalledProgram p, bool preferQuiet, ILogSink log, CancellationToken ct = default)
    {
        var cmd = GetCommand(p, preferQuiet);
        if (!TryParse(cmd, out var file, out var args))
            return new(false, null, true, "Для этой программы не зарегистрирован штатный деинсталлятор. Данные можно удалить в разделе «Остатки».");

        try
        {
            log.Info($"Запуск деинсталлятора «{p.Name}»: {file} {args}".TrimEnd());
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            var dir = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null;
            if (dir != null && Directory.Exists(dir)) psi.WorkingDirectory = dir;

            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
            int? exit = proc?.ExitCode;
            bool still = p.StillRegistered();
            log.Info($"Деинсталлятор завершился, код {exit?.ToString() ?? "?"}; запись в реестре {(still ? "осталась" : "удалена")}.");

            string msg = still
                ? "Деинсталлятор завершил работу, но программа всё ещё зарегистрирована: удаление могли отменить, либо оно продолжается в фоне. Нажмите «Обновить список», когда оно закончится."
                : "Программа удалена штатным деинсталлятором.";
            return new(true, exit, still, msg);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log.Warn("Пользователь отклонил запрос прав администратора (UAC).");
            return new(false, null, true, "Запрос прав администратора отклонён — деинсталлятор не запущен.");
        }
        catch (Win32Exception ex)
        {
            log.Error($"Не удалось запустить деинсталлятор: {ex.Message}");
            return new(false, null, true, $"Не удалось запустить деинсталлятор: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new(true, null, p.StillRegistered(), "Ожидание деинсталлятора прервано.");
        }
    }
}
