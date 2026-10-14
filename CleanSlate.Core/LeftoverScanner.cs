namespace CleanSlate.Core;

public sealed record ScanRoot(string Path, string Category);

public static class ScanRoots
{
    public static List<ScanRoot> Build(bool includeProgramData)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<ScanRoot>
        {
            new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Данные программы (AppData\Local)"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),      @"Настройки (AppData\Roaming)"),
            new(Path.Combine(profile, "AppData", "LocalLow"),                              @"Данные программы (AppData\LocalLow)"),
            new(Path.Combine(profile, "Saved Games"),                                      "Сохранения игр (Saved Games)"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),          "Документы"),
        };
        if (includeProgramData)
            list.Add(new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Общие данные (ProgramData)"));
        return list;
    }
}

public enum MatchKind { None, Partial, Exact }

public static class NameMatcher
{
    static readonly HashSet<string> Generic = new()
    { "program", "programs", "application", "app", "apps", "bin", "game", "games", "data", "files", "install", "installer", "setup", "client", "launcher", "release", "build" };

    public static string Norm(string? s) =>
        new(( s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Нормализованные имена, с которыми сравниваются папки: название программы и (если не «общее») имя каталога установки.</summary>
    public static List<string> Targets(ProgramQuery q)
    {
        var t = new List<string>();
        var n = Norm(q.Name);
        if (n.Length >= 3) t.Add(n);
        var loc = (q.InstallLocation ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        if (loc.Length > 0)
        {
            var leaf = Norm(Path.GetFileName(loc));
            if (leaf.Length >= 5 && !Generic.Contains(leaf) && !t.Contains(leaf)) t.Add(leaf);
        }
        return t;
    }

    public static MatchKind Match(string folder, IReadOnlyList<string> targets)
    {
        var n = Norm(folder);
        if (n.Length < 3) return MatchKind.None;
        if (targets.Contains(n)) return MatchKind.Exact;
        foreach (var t in targets)
        {
            if (t.Length >= 4 && n.Length > t.Length && n.Contains(t)) return MatchKind.Partial;   // «TestGameBackup»
            if (n.Length >= 6 && t.Length > n.Length && t.Contains(n)) return MatchKind.Partial;
        }
        return MatchKind.None;
    }

    public static bool PublisherMatches(string folder, string publisher)
    {
        var f = Norm(folder); var p = Norm(publisher);
        if (f.Length < 4 || p.Length < 4) return false;
        return f == p || p.Contains(f) || f.Contains(p);
    }
}

/// <summary>
/// Ищет каталоги, связанные с программой. Совпадения имени недостаточно для статуса «подтверждено»:
/// нужен второй независимый признак (каталог издателя в пути либо файлы внутри, ссылающиеся на путь установки/издателя).
/// </summary>
public sealed class LeftoverScanner
{
    static readonly string[] MarkerExt = { ".ini", ".cfg", ".conf", ".json", ".xml", ".log", ".txt", ".config", ".yaml", ".yml" };

    readonly IReadOnlyList<ScanRoot> _roots;
    readonly SafetyGuard _guard;
    readonly ILogSink _log;
    readonly bool _scanInstallDir;

    public LeftoverScanner(IReadOnlyList<ScanRoot> roots, SafetyGuard guard, ILogSink? log = null, bool scanInstallDir = true)
    {
        _roots = roots; _guard = guard; _log = log ?? NullLog.Instance; _scanInstallDir = scanInstallDir;
    }

    public Task<List<Leftover>> ScanAsync(ProgramQuery q, CancellationToken ct = default) =>
        Task.Run(() => Scan(q, ct), ct);

    public List<Leftover> Scan(ProgramQuery q, CancellationToken ct = default)
    {
        var found = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = NameMatcher.Targets(q);
        if (targets.Count == 0) { _log.Warn("Слишком короткое название — поиск не выполнялся."); return found; }

        _log.Info($"Поиск остатков для «{q.Name}» (издатель: {(string.IsNullOrWhiteSpace(q.Publisher) ? "не указан" : q.Publisher)})");

        AddInstallDir(q, found, seen);

        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.Path)) { _log.Info($"Каталог поиска отсутствует, пропущен: {root.Path}"); continue; }

            foreach (var d1 in SafeDirs(root.Path))
            {
                ct.ThrowIfCancellationRequested();
                var n1 = Path.GetFileName(d1);
                if (SafetyGuard.IsProtectedName(n1)) continue;

                Consider(d1, null, root, q, targets, found, seen, ct);
                foreach (var d2 in SafeDirs(d1))
                {
                    ct.ThrowIfCancellationRequested();
                    Consider(d2, n1, root, q, targets, found, seen, ct);
                }
            }
        }

        foreach (var l in found)
        {
            ct.ThrowIfCancellationRequested();
            var (bytes, files, partial) = DirSize(l.Path, ct);
            l.SizeBytes = bytes; l.FileCount = files; l.SizeIsPartial = partial;
        }

        found = found.OrderBy(l => l.Confidence).ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList();
        _log.Info($"Найдено каталогов: {found.Count} (подтверждённых: {found.Count(f => f.Confidence == Confidence.Verified)})");
        return found;
    }

    void AddInstallDir(ProgramQuery q, List<Leftover> found, HashSet<string> seen)
    {
        var loc = (q.InstallLocation ?? "").Trim().Trim('"');
        if (!_scanInstallDir || loc.Length == 0 || !Directory.Exists(loc)) return;
        if (!_guard.IsDeletable(loc, true, out var why)) { _log.Warn($"Каталог установки защищён и не предлагается: {loc} ({why})"); return; }
        if (!seen.Add(Path.GetFullPath(loc))) return;

        found.Add(new Leftover
        {
            Path = loc, IsInstallDir = true, Category = "Каталог установки",
            Confidence = q.InstallLocationTrusted ? Confidence.Verified : Confidence.Unverified,
            IsSelected = q.InstallLocationTrusted,
            Reason = q.InstallLocationTrusted
                ? "путь взят из реестра, программа уже удалена"
                : "путь установки; программа ещё установлена или путь введён вручную — для её удаления используйте деинсталлятор",
        });
    }

    void Consider(string dir, string? parentName, ScanRoot root, ProgramQuery q, List<string> targets,
                  List<Leftover> found, HashSet<string> seen, CancellationToken ct)
    {
        var kind = NameMatcher.Match(Path.GetFileName(dir), targets);
        if (kind == MatchKind.None) return;
        if (!_guard.IsDeletable(dir, false, out var why)) { _log.Info($"Пропущен защищённый путь {dir}: {why}"); return; }
        if (!seen.Add(Path.GetFullPath(dir))) return;

        var reasons = new List<string> { kind == MatchKind.Exact ? "точное совпадение имени" : "частичное совпадение имени (возможно, другая программа)" };
        bool evidence = false;
        if (parentName != null && NameMatcher.PublisherMatches(parentName, q.Publisher))
        { reasons.Add("родительский каталог совпадает с издателем"); evidence = true; }
        if (kind == MatchKind.Exact && HasMarker(dir, q, ct))
        { reasons.Add("файлы внутри ссылаются на путь установки или издателя"); evidence = true; }

        var conf = kind == MatchKind.Exact && evidence ? Confidence.Verified : Confidence.Unverified;
        if (conf == Confidence.Unverified && kind == MatchKind.Exact) reasons.Add("нет второго подтверждающего признака");

        found.Add(new Leftover
        {
            Path = dir, Category = root.Category, Confidence = conf,
            Reason = string.Join("; ", reasons), IsSelected = conf == Confidence.Verified,
        });
    }

    static bool HasMarker(string dir, ProgramQuery q, CancellationToken ct)
    {
        var needles = new List<string>();
        var loc = (q.InstallLocation ?? "").Trim().Trim('"').TrimEnd('\\');
        if (loc.Length >= 6) { needles.Add(loc); needles.Add(loc.Replace("\\", "\\\\")); }
        if (NameMatcher.Norm(q.Publisher).Length >= 4) needles.Add(q.Publisher.Trim());
        if (needles.Count == 0) return false;

        try
        {
            int n = 0;
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles())
            {
                if (++n > 25) break;
                ct.ThrowIfCancellationRequested();
                if (!MarkerExt.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase) || fi.Length > 131072) continue;
                using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var text = sr.ReadToEnd();
                if (needles.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* нечитаемые файлы — просто нет признака */ }
        return false;
    }

    List<string> SafeDirs(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _log.Warn($"Нет доступа к каталогу {path}: {ex.Message}");
            return new();
        }
    }

    public static (long Bytes, int Files, bool Partial) DirSize(string path, CancellationToken ct = default)
    {
        long total = 0; int count = 0; bool partial = false;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            try
            {
                foreach (var fsi in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (fsi is FileInfo fi) { total += fi.Length; count++; }
                    else stack.Push(fsi.FullName);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            { partial = true; }
        }
        return (total, count, partial);
    }
}
