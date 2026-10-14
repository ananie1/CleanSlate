using CleanSlate.Core;

namespace CleanSlate.Tests;

/// <summary>Временная «пользовательская папка» с теми же подкаталогами, что ищет программа.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "CleanSlateTests_" + Guid.NewGuid().ToString("N"));
    public ScanRoot[] Roots { get; }
    public SafetyGuard Guard { get; }

    public Sandbox()
    {
        Roots = new[]
        {
            new ScanRoot(Path.Combine(Root, "Local"), "Local"),
            new ScanRoot(Path.Combine(Root, "Roaming"), "Roaming"),
            new ScanRoot(Path.Combine(Root, "LocalLow"), "LocalLow"),
            new ScanRoot(Path.Combine(Root, "SavedGames"), "Saved Games"),
            new ScanRoot(Path.Combine(Root, "Docs"), "Документы"),
        };
        foreach (var r in Roots) Directory.CreateDirectory(r.Path);
        Guard = SafetyGuard.CreateDefault(Roots.Select(r => r.Path));
    }

    public string Dir(string relative, params (string Name, int Size)[] files)
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(p);
        foreach (var (n, s) in files) File.WriteAllBytes(Path.Combine(p, n), new byte[s]);
        return p;
    }

    /// <summary>Вымышленная игра TestGame в нескольких каталогах + похожая TestGameBackup + посторонняя OtherGame.</summary>
    public (string localPub, string roamingMarked, string lowPub, string saved, string docs, string backup) CreateTestGame(string installDirForMarker)
    {
        var localPub = Dir(@"Local\TestPublisher\TestGame", ("save1.dat", 10_000), ("cache.bin", 200_000));
        var roaming = Dir(@"Roaming\TestGame", ("big.bin", 50_000));
        File.WriteAllText(Path.Combine(roaming, "settings.ini"), $"InstallLocation={installDirForMarker}");
        var low = Dir(@"LocalLow\TestPublisher\TestGame", ("a.txt", 1_000));
        var saved = Dir(@"SavedGames\TestGame", ("slot1.sav", 5_000));
        var docs = Dir(@"Docs\My Games\TestGame", ("profile.cfg", 300));
        var backup = Dir(@"Local\TestGameBackup", ("backup.zip", 123_456));
        Dir(@"Roaming\OtherGame", ("x.dat", 100));
        return (localPub, roaming, low, saved, docs, backup);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { /* временные файлы — не критично */ }
    }
}
