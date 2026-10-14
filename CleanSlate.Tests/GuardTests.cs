using CleanSlate.Core;
using Xunit;

namespace CleanSlate.Tests;

public class GuardTests
{
    [Fact]
    public void RefusesSystemAndUserRoots()
    {
        using var sb = new Sandbox();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var p in new[] { Path.GetPathRoot(win)!, win, Path.Combine(win, "System32"), pf, profile,
                                  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            Assert.False(sb.Guard.IsDeletable(p, true, out _), p);
    }

    [Fact]
    public void RefusesScanRootsThemselvesAndPathsOutsideRoots()
    {
        using var sb = new Sandbox();
        Assert.False(sb.Guard.IsDeletable(sb.Roots[0].Path, false, out _));
        Assert.False(sb.Guard.IsDeletable(Path.Combine(sb.Root, "Elsewhere", "TestGame"), false, out _));
    }

    [Fact]
    public void RefusesSharedNamesEvenInsideAllowedRoots()
    {
        using var sb = new Sandbox();
        var ms = sb.Dir(@"Local\Microsoft", ("x.dat", 1));
        Assert.False(sb.Guard.IsDeletable(ms, false, out _));
    }

    [Fact]
    public void AllowsNormalGameFolder()
    {
        using var sb = new Sandbox();
        var p = sb.Dir(@"Local\TestPublisher\TestGame", ("a", 1));
        Assert.True(sb.Guard.IsDeletable(p, false, out var why), why);
    }
}
