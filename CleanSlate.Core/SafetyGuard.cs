namespace CleanSlate.Core;

/// <summary>
/// Последний рубеж безопасности: решает, можно ли вообще предлагать/удалять путь.
/// Проверяется и при поиске, и повторно непосредственно перед удалением.
/// </summary>
public sealed class SafetyGuard
{
    static readonly StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "WindowsApps", "Packages", "Temp", "Programs", "Common Files",
        "Microsoft Shared", "Package Cache", "System32", "SysWOW64", "WinSxS", "Windows NT",
        "Windows Defender", "Internet Explorer", "ProgramData", "Users", "Program Files", "Program Files (x86)",
    };

    readonly List<string> _roots;
    readonly List<string> _exactProtected;
    readonly List<string> _treeProtected;

    /// <param name="allowedRoots">Корни поиска: удалять можно только строго внутри них (кроме каталога установки).</param>
    /// <param name="exactProtected">Эти каталоги и любые их родители удалять нельзя.</param>
    /// <param name="treeProtected">Эти каталоги и всё внутри них удалять нельзя.</param>
    public SafetyGuard(IEnumerable<string> allowedRoots, IEnumerable<string> exactProtected, IEnumerable<string> treeProtected)
    {
        _roots = allowedRoots.Select(Norm).ToList();
        _exactProtected = exactProtected.Select(Norm).ToList();
        _treeProtected = treeProtected.Select(Norm).ToList();
    }

    public static SafetyGuard CreateDefault(IEnumerable<string> allowedRoots)
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var exact = new[]
        {
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.System), F(Environment.SpecialFolder.SystemX86),
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonProgramFiles), F(Environment.SpecialFolder.CommonProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), profile,
            F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.MyDocuments), F(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "AppData"), Path.Combine(profile, "AppData", "LocalLow"), Path.Combine(profile, "Saved Games"),
            Path.GetDirectoryName(profile) ?? "", Environment.GetEnvironmentVariable("PUBLIC") ?? "",
        }.Where(s => !string.IsNullOrWhiteSpace(s));
        return new SafetyGuard(allowedRoots, exact, new[] { F(Environment.SpecialFolder.Windows) });
    }

    public static bool IsProtectedName(string leaf) => ProtectedNames.Contains(leaf);

    public bool IsDeletable(string path, bool isInstallDir, out string reason)
    {
        reason = "";
        string full;
        try { full = Norm(path); }
        catch { reason = "Некорректный путь"; return false; }

        var root = Path.GetPathRoot(full) ?? "";
        if (full.Equals(root, Cmp)) { reason = "Корень диска"; return false; }

        var leaf = Path.GetFileName(full);
        if (IsProtectedName(leaf)) { reason = $"Системное или общее имя каталога «{leaf}»"; return false; }

        foreach (var p in _exactProtected)
        {
            if (full.Equals(p, Cmp)) { reason = "Защищённый системный/пользовательский каталог"; return false; }
            if (IsInside(p, full)) { reason = "Каталог содержит защищённый системный каталог"; return false; }
        }
        foreach (var p in _treeProtected)
            if (full.Equals(p, Cmp) || IsInside(full, p)) { reason = "Внутри системного каталога Windows"; return false; }

        var self = Norm(AppContext.BaseDirectory);
        if (full.Equals(self, Cmp) || IsInside(self, full)) { reason = "Каталог самого приложения CleanSlate"; return false; }

        if (!isInstallDir && !_roots.Any(r => IsInside(full, r))) { reason = "Вне разрешённых каталогов поиска"; return false; }

        try
        {
            if (Directory.Exists(full) && (new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
            { reason = "Символическая ссылка/junction — не удаляется"; return false; }
        }
        catch { /* нет доступа к атрибутам — дальше решит удаление */ }

        return true;
    }

    static string Norm(string p)
    {
        var f = Path.GetFullPath(p);
        var root = Path.GetPathRoot(f) ?? "";
        if (f.Length > root.Length) f = f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return f;
    }

    static bool IsInside(string child, string parent) =>
        child.Length > parent.Length &&
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Cmp);
}
