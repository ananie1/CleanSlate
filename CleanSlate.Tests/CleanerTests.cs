using System.Security.AccessControl;
using System.Security.Principal;
using CleanSlate.Core;
using Xunit;

namespace CleanSlate.Tests;

public class CleanerTests
{
    static Leftover Item(string path, bool selected = true) =>
        new() { Path = path, Category = "t", Confidence = Confidence.Verified, IsSelected = selected };

    static Cleaner Make(Sandbox sb) => new(sb.Guard);

    [Fact]
    public void DeletesOnlyWhatWasPassed_BackupSurvives()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(@"C:\Games\TestGame");
        var res = Make(sb).Clean(new[] { Item(g.localPub), Item(g.lowPub) });

        Assert.False(Directory.Exists(g.localPub));
        Assert.False(Directory.Exists(g.lowPub));
        Assert.True(Directory.Exists(g.backup));       // TestGameBackup не тронут
        Assert.True(Directory.Exists(g.saved));
        Assert.All(res.Entries, e => Assert.Equal(CleanStatus.Deleted, e.Status));
        Assert.Equal(211_000, res.FreedBytes);
    }

    [Fact]
    public void ReadOnlyFilesAreDeleted()
    {
        using var sb = new Sandbox();
        var d = sb.Dir(@"Roaming\TestGame", ("ro.dat", 10));
        File.SetAttributes(Path.Combine(d, "ro.dat"), FileAttributes.ReadOnly);
        var res = Make(sb).Clean(new[] { Item(d) });
        Assert.Equal(CleanStatus.Deleted, res.Entries.Single().Status);
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void CancelledOperationDeletesNothing()
    {
        using var sb = new Sandbox();
        var g = sb.CreateTestGame(@"C:\Games\TestGame");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var res = Make(sb).Clean(new[] { Item(g.localPub), Item(g.saved) }, cts.Token);
        Assert.True(Directory.Exists(g.localPub));
        Assert.True(Directory.Exists(g.saved));
        Assert.All(res.Entries, e => Assert.Equal(CleanStatus.Cancelled, e.Status));
    }

    [Fact]
    public void NonexistentPathIsReportedNotThrown()
    {
        using var sb = new Sandbox();
        var ghost = Path.Combine(sb.Roots[0].Path, "TestPublisher", "Ghost");
        var res = Make(sb).Clean(new[] { Item(ghost) });
        Assert.Equal(CleanStatus.AlreadyGone, res.Entries.Single().Status);
    }

    [Fact]
    public void ProtectedPathIsSkippedEvenIfSomeoneSelectedIt()
    {
        using var sb = new Sandbox();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var res = Make(sb).Clean(new[] { Item(win), Item(Path.Combine(win, "System32")) });
        Assert.All(res.Entries, e => Assert.Equal(CleanStatus.Skipped, e.Status));
        Assert.True(Directory.Exists(win));
    }

    [Fact]
    public void OneFailureDoesNotStopTheOthers()
    {
        using var sb = new Sandbox();
        var a = sb.Dir(@"Local\TestPublisher\TestGame", ("a.bin", 1000));
        var b = sb.Dir(@"Roaming\TestGame", ("b.bin", 2000));
        var c = sb.Dir(@"SavedGames\TestGame", ("c.bin", 3000));

        using var locked = new FileStream(Path.Combine(b, "b.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        var res = Make(sb).Clean(new[] { Item(a), Item(b), Item(c) });

        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(c));
        Assert.True(Directory.Exists(b));
        var failed = res.Entries.Single(e => e.Item.Path == b);
        Assert.Equal(CleanStatus.Failed, failed.Status);
        Assert.NotEmpty(failed.Errors);
        Assert.Equal(2, res.Entries.Count(e => e.Status == CleanStatus.Deleted));
        Assert.Contains("ОШИБКА", res.ToReport());
    }

    [Fact]
    public void AccessDeniedIsReportedWithoutCrash()
    {
        using var sb = new Sandbox();
        var denied = sb.Dir(@"Roaming\TestGame", ("secret.dat", 100));
        var ok = sb.Dir(@"SavedGames\TestGame", ("c.bin", 10));

        var di = new DirectoryInfo(denied);
        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        var sec = di.GetAccessControl(); sec.AddAccessRule(rule); di.SetAccessControl(sec);
        try
        {
            var res = Make(sb).Clean(new[] { Item(denied), Item(ok) });
            Assert.True(Directory.Exists(denied));
            Assert.False(Directory.Exists(ok));
            Assert.Equal(CleanStatus.Failed, res.Entries[0].Status);
            Assert.Equal(CleanStatus.Deleted, res.Entries[1].Status);
        }
        finally
        {
            var s2 = di.GetAccessControl(); s2.RemoveAccessRule(rule); di.SetAccessControl(s2);
        }
    }
}
