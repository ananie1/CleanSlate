using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace CleanSlate.Core;

public static class Format
{
    public static string Size(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} Б" : $"{v:0.##} {units[i]}";
    }
}

public sealed class InstalledProgram
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public string SubKey { get; init; } = "";

    public string Subtitle => string.Join(" · ", new[] { Publisher, Version }.Where(s => s.Length > 0));

    /// <summary>true, если запись в реестре ещё существует. При ошибке чтения считаем, что существует (консервативно).</summary>
    public bool StillRegistered()
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(Hive, View);
            using var k = b.OpenSubKey(SubKey);
            return k != null;
        }
        catch { return true; }
    }
}

/// <summary>Что искать: берётся из реестра либо вводится вручную (для уже удалённых программ).</summary>
public sealed record ProgramQuery(string Name, string Publisher = "", string InstallLocation = "", bool InstallLocationTrusted = false)
{
    public static ProgramQuery FromProgram(InstalledProgram p, bool trusted = false) =>
        new(p.Name, p.Publisher, p.InstallLocation, trusted);
}

public enum Confidence { Verified = 0, Unverified = 1 }

public sealed class Leftover : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Category { get; init; } = "";
    public Confidence Confidence { get; init; }
    public string Reason { get; init; } = "";
    public bool IsInstallDir { get; init; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool SizeIsPartial { get; set; }

    bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }

    public string SizeText => (SizeIsPartial ? "≥ " : "") + Format.Size(SizeBytes) + (FileCount == 0 ? " (пусто)" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LeftoverSet : INotifyPropertyChanged
{
    public ObservableCollection<Leftover> Items { get; } = new();
    public CancellationTokenSource? Cts { get; set; }

    bool _busy; string _status = "";
    public bool IsBusy { get => _busy; set { _busy = value; Raise(); } }
    public string Status { get => _status; set { _status = value; Raise(); } }

    public IEnumerable<Leftover> Selected => Items.Where(i => i.IsSelected);

    public string Summary => Items.Count == 0 ? "" :
        $"Отмечено: {Selected.Count()} из {Items.Count} · {Format.Size(Selected.Sum(i => i.SizeBytes))}";

    public LeftoverSet() => Items.CollectionChanged += (_, _) => Raise(nameof(Summary));

    public void Replace(IEnumerable<Leftover> items)
    {
        foreach (var o in Items) o.PropertyChanged -= OnItem;
        Items.Clear();
        foreach (var i in items) { i.PropertyChanged += OnItem; Items.Add(i); }
        Raise(nameof(Summary));
    }

    public void RemoveWhere(Func<Leftover, bool> pred)
    {
        foreach (var i in Items.Where(pred).ToList()) { i.PropertyChanged -= OnItem; Items.Remove(i); }
    }

    public void SelectVerifiedOnly() { foreach (var i in Items) i.IsSelected = i.Confidence == Confidence.Verified; }
    public void SelectAll() { foreach (var i in Items) i.IsSelected = true; }
    public void SelectNone() { foreach (var i in Items) i.IsSelected = false; }

    void OnItem(object? s, PropertyChangedEventArgs e) => Raise(nameof(Summary));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
