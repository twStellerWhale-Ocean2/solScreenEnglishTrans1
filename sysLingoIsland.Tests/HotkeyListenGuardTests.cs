using System.Collections.Generic;
using LingoIsland.Capture;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 指定快捷鍵監聽期間之熱鍵暫停/恢復守衛（Issue #89）——冪等、對稱、恢復必達；純邏輯可單元測試。
/// </summary>
public class HotkeyListenGuardTests
{
    private static HotkeyListenGuard Make(List<string> log) =>
        new(() => log.Add("suspend"), () => log.Add("resume"));

    [Fact]
    public void Start_Suspends_Once()
    {
        var log = new List<string>();
        var g = Make(log);
        g.OnListeningChanged(true);
        Assert.Equal(new[] { "suspend" }, log);
        Assert.True(g.IsSuspended);
    }

    [Fact]
    public void Start_Then_Stop_Suspends_Then_Resumes()
    {
        var log = new List<string>();
        var g = Make(log);
        g.OnListeningChanged(true);
        g.OnListeningChanged(false);
        Assert.Equal(new[] { "suspend", "resume" }, log);
        Assert.False(g.IsSuspended);
    }

    [Fact]
    public void Repeated_Start_Suspends_Only_Once()
    {
        var log = new List<string>();
        var g = Make(log);
        g.OnListeningChanged(true);
        g.OnListeningChanged(true);
        Assert.Equal(new[] { "suspend" }, log);
        Assert.True(g.IsSuspended);
    }

    [Fact]
    public void Stop_Without_Start_Does_Not_Resume()
    {
        var log = new List<string>();
        var g = Make(log);
        g.OnListeningChanged(false);
        Assert.Empty(log);
        Assert.False(g.IsSuspended);
    }

    [Fact]
    public void Repeated_Stop_Resumes_Only_Once()
    {
        var log = new List<string>();
        var g = Make(log);
        g.OnListeningChanged(true);
        g.OnListeningChanged(false);
        g.OnListeningChanged(false);
        Assert.Equal(new[] { "suspend", "resume" }, log);
        Assert.False(g.IsSuspended);
    }

    [Fact]
    public void Cycle_Is_Balanced_And_Restore_Always_Follows_Suspend()
    {
        var log = new List<string>();
        var g = Make(log);
        g.OnListeningChanged(true);
        g.OnListeningChanged(false);
        g.OnListeningChanged(true);
        g.OnListeningChanged(false);
        Assert.Equal(new[] { "suspend", "resume", "suspend", "resume" }, log);
        Assert.False(g.IsSuspended);
    }
}
