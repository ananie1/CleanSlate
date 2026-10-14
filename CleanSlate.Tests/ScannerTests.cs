using CleanSlate.Core;
using Xunit;

namespace CleanSlate.Tests;

public class ScannerTests
{
    const string Install = @"C:\Games\TestGame";   // в тестах не существует

    static LeftoverScanner Scanner(Sandbox sb) => new(sb.Roots, sb.Guard);

    [Fact]
    public async Task FindsTestGameInAllUserDirectories()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(Install);
        var found = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher", Install));
        var paths = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(g.localPub, paths);
        Assert.Contains(g.roamingMarked, paths);
        Assert.Contains(g.lowPub, paths);
        Assert.Contains(g.saved, paths);
        Assert.Contains(g.docs, paths);
        Assert.DoesNotContain(found, f => f.Path.EndsWith("OtherGame"));
    }

    [Fact]
    public async Task SizesAreReported()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(Install);
        var found = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher", Install));
        var l = found.Single(f => f.Path == g.localPub);
        Assert.Equal(210_000, l.SizeBytes);
        Assert.Equal(2, l.FileCount);
    }

    [Fact]
    public async Task SimilarNamedBackupIsUnverifiedAndNotSelected()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(Install);
        var found = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher", Install));
        var b = found.Single(f => f.Path == g.backup);
        Assert.Equal(Confidence.Unverified, b.Confidence);
        Assert.False(b.IsSelected);
    }

    [Fact]
    public async Task NameMatchAloneIsNotEnough_ButSecondSignalVerifies()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(Install);
        var found = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher", Install));

        var plain = found.Single(f => f.Path == g.saved);      // только имя
        Assert.Equal(Confidence.Unverified, plain.Confidence); Assert.False(plain.IsSelected);

        var byPublisher = found.Single(f => f.Path == g.localPub);   // имя + каталог издателя
        Assert.Equal(Confidence.Verified, byPublisher.Confidence); Assert.True(byPublisher.IsSelected);

        var byMarker = found.Single(f => f.Path == g.roamingMarked); // имя + файл со ссылкой на путь установки
        Assert.Equal(Confidence.Verified, byMarker.Confidence);
    }

    [Fact]
    public async Task ProgramWithoutPublisher_NothingIsPreselectedByPublisher()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(Install);
        var found = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "", ""));
        Assert.NotEmpty(found);
        Assert.All(found, f => { Assert.Equal(Confidence.Unverified, f.Confidence); Assert.False(f.IsSelected); });
    }

    [Fact]
    public async Task AlreadyUninstalled_RemainingInstallDirIsVerifiedOnlyWhenTrusted()
    {
        using var sb = new Sandbox();
        var install = sb.Dir(@"Install\TestGame", ("leftover.dll", 777));

        var trusted = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher", install, true));
        var t = trusted.Single(f => f.IsInstallDir);
        Assert.Equal(Confidence.Verified, t.Confidence); Assert.True(t.IsSelected);

        var untrusted = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher", install, false));
        var u = untrusted.Single(f => f.IsInstallDir);
        Assert.Equal(Confidence.Unverified, u.Confidence); Assert.False(u.IsSelected);
    }

    [Fact]
    public async Task EmptyDirectoryIsListedWithZeroSize()
    {
        using var sb = new Sandbox();
        var empty = sb.Dir(@"Roaming\TestGame");
        var found = await Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher"));
        var e = found.Single(f => f.Path == empty);
        Assert.Equal(0, e.SizeBytes); Assert.Equal(0, e.FileCount);
    }

    [Fact]
    public async Task MissingRootsDoNotThrow()
    {
        using var sb = new Sandbox();
        var roots = new[] { new ScanRoot(Path.Combine(sb.Root, "NoSuchFolder"), "x") };
        var scanner = new LeftoverScanner(roots, SafetyGuard.CreateDefault(roots.Select(r => r.Path)));
        Assert.Empty(await scanner.ScanAsync(new ProgramQuery("TestGame", "TestPublisher")));
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        using var sb = new Sandbox();
        sb.CreateTestGame(Install);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Scanner(sb).ScanAsync(new ProgramQuery("TestGame", "TestPublisher"), cts.Token));
    }
}
