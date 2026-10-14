using System.Text;

namespace CleanSlate.Core;

public enum CleanStatus { Deleted, DeletedPartially, Failed, Skipped, AlreadyGone, Cancelled }

public sealed class CleanEntry
{
    public CleanEntry(Leftover item) => Item = item;
    public Leftover Item { get; }
    public CleanStatus Status { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Errors { get; } = new();
    public string Message { get; set; } = "";
}

public sealed class CleanupResult
{
    public List<CleanEntry> Entries { get; } = new();
    public long FreedBytes => Entries.Sum(e => e.FreedBytes);
    public long? FreeSpaceDelta { get; set; }

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Удалено полностью: {Entries.Count(e => e.Status == CleanStatus.Deleted)} из {Entries.Count}");
        sb.AppendLine($"Освобождено (сумма размеров удалённых файлов): {Format.Size(FreedBytes)}");
        if (FreeSpaceDelta is long d)
            sb.AppendLine($"Изменение свободного места на дисках: {(d >= 0 ? "+" : "−")}{Format.Size(Math.Abs(d))} (приблизительно: на диск могут писать другие процессы)");
        var bad = Entries.Count(e => e.Status is CleanStatus.Failed or CleanStatus.DeletedPartially);
        if (bad > 0) sb.AppendLine($"Ошибки: {bad} каталог(ов) удалены не полностью — подробности ниже и в журнале.");
        sb.AppendLine();

        foreach (var e in Entries)
        {
            var label = e.Status switch
            {
                CleanStatus.Deleted => "[УДАЛЕНО]       ",
                CleanStatus.DeletedPartially => "[ЧАСТИЧНО]      ",
                CleanStatus.Failed => "[ОШИБКА]        ",
                CleanStatus.Skipped => "[ПРОПУЩЕНО]     ",
                CleanStatus.AlreadyGone => "[УЖЕ НЕТ]       ",
                _ => "[ОТМЕНЕНО]       ",
            };
            sb.AppendLine($"{label}{e.Item.Path}  ({Format.Size(e.FreedBytes)})");
            if (e.Message.Length > 0) sb.AppendLine($"                 {e.Message}");
            foreach (var err in e.Errors.Take(10)) sb.AppendLine($"                 ! {err}");
            if (e.Errors.Count > 10) sb.AppendLine($"                 ! …и ещё {e.Errors.Count - 10} ошибок (см. журнал)");
        }
        return sb.ToString();
    }
}

/// <summary>Удаляет выбранные каталоги. Ошибка в одном каталоге (или файле) не прерывает остальные.</summary>
public sealed class Cleaner
{
    readonly SafetyGuard _guard;
    readonly ILogSink _log;

    public Cleaner(SafetyGuard guard, ILogSink? log = null) { _guard = guard; _log = log ?? NullLog.Instance; }

    public CleanupResult Clean(IEnumerable<Leftover> items, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var list = items.ToList();
        var result = new CleanupResult();
        var drives = list.Select(i => SafeRoot(i.Path)).Where(r => r != null).Select(r => r!)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var before = FreeSpace(drives);
        _log.Info($"Начата очистка: каталогов {list.Count}");

        foreach (var item in list)
        {
            var entry = new CleanEntry(item);
            result.Entries.Add(entry);

            if (ct.IsCancellationRequested) { entry.Status = CleanStatus.Cancelled; entry.Message = "операция отменена"; continue; }

            if (!_guard.IsDeletable(item.Path, item.IsInstallDir, out var why))
            {
                entry.Status = CleanStatus.Skipped; entry.Message = $"защита: {why}";
                _log.Warn($"Пропущено (защита) {item.Path}: {why}");
                continue;
            }
            if (!Directory.Exists(item.Path))
            {
                entry.Status = CleanStatus.AlreadyGone; entry.Message = "каталог уже отсутствует";
                _log.Info($"Уже отсутствует: {item.Path}");
                continue;
            }

            progress?.Report($"Удаление: {item.Path}");
            try
            {
                entry.FreedBytes = DeleteTree(new DirectoryInfo(item.Path), entry.Errors);
            }
            catch (Exception ex)
            {
                entry.Errors.Add(Describe(ex));
            }

            bool exists = Directory.Exists(item.Path);
            if (!exists && entry.Errors.Count == 0) { entry.Status = CleanStatus.Deleted; _log.Info($"Удалено: {item.Path} ({Format.Size(entry.FreedBytes)})"); }
            else
            {
                entry.Status = entry.FreedBytes > 0 ? CleanStatus.DeletedPartially : CleanStatus.Failed;
                _log.Error($"{(entry.Status == CleanStatus.Failed ? "Не удалено" : "Удалено частично")}: {item.Path}; освобождено {Format.Size(entry.FreedBytes)}");
                foreach (var e in entry.Errors) _log.Error($"    {e}");
            }
        }

        var after = FreeSpace(drives);
        if (before != null && after != null) result.FreeSpaceDelta = after - before;
        _log.Info($"Очистка завершена. Освобождено по размеру файлов: {Format.Size(result.FreedBytes)}");
        return result;
    }

    static long DeleteTree(DirectoryInfo dir, List<string> errors)
    {
        long freed = 0;
        int startErrors = errors.Count;
        List<FileSystemInfo> children;
        try { children = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (IsIo(ex)) { errors.Add($"{dir.FullName}: {Describe(ex)}"); return 0; }

        foreach (var c in children)
        {
            try
            {
                if ((c.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // ссылку удаляем саму, в целевой каталог не заходим
                    if (c is DirectoryInfo) Directory.Delete(c.FullName, false); else File.Delete(c.FullName);
                }
                else if (c is DirectoryInfo sub) freed += DeleteTree(sub, errors);
                else
                {
                    var fi = (FileInfo)c;
                    long len = fi.Length;
                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    freed += len;
                }
            }
            catch (Exception ex) when (IsIo(ex)) { errors.Add($"{c.FullName}: {Describe(ex)}"); }
        }

        try { dir.Delete(); }
        catch (Exception ex) when (IsIo(ex)) { if (errors.Count == startErrors) errors.Add($"{dir.FullName}: {Describe(ex)}"); }
        return freed;
    }

    static bool IsIo(Exception ex) => ex is UnauthorizedAccessException or IOException or System.Security.SecurityException;

    static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "нет доступа (отказано в разрешении)",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "файл занят другим процессом",
        _ => ex.Message,
    };

    static string? SafeRoot(string path) { try { return Path.GetPathRoot(Path.GetFullPath(path)); } catch { return null; } }

    static long? FreeSpace(IEnumerable<string> roots)
    {
        long sum = 0; bool any = false;
        foreach (var r in roots)
        {
            try { sum += new DriveInfo(r).AvailableFreeSpace; any = true; } catch { }
        }
        return any ? sum : null;
    }
}
