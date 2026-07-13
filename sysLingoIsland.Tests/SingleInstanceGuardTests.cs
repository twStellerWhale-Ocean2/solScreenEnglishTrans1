using System;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 單一實例守衛行為（Issue #6）：以具名 Mutex 偵測既有實例。
/// 每個測試用唯一 Mutex 名避免跨測試干擾。
/// </summary>
public class SingleInstanceGuardTests
{
    private static string UniqueName() => $@"Local\LingoIsland.Test.{Guid.NewGuid():N}";

    [Fact]
    public void Acquire_FirstTime_IsFirstInstanceTrue()
    {
        var name = UniqueName();
        using var guard = SingleInstanceGuard.Acquire(name);
        Assert.True(guard.IsFirstInstance);
    }

    [Fact]
    public void Acquire_SecondTime_WhileFirstHeld_IsFirstInstanceFalse()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.Acquire(name);
        using var second = SingleInstanceGuard.Acquire(name);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance); // 既有實例仍持有 → 判定為第二實例
    }

    [Fact]
    public void Acquire_AfterFirstReleased_IsFirstInstanceTrueAgain()
    {
        var name = UniqueName();

        var first = SingleInstanceGuard.Acquire(name);
        Assert.True(first.IsFirstInstance);
        first.Dispose(); // 首個實例結束、命名物件消滅

        using var next = SingleInstanceGuard.Acquire(name);
        Assert.True(next.IsFirstInstance); // 釋放後再啟動 → 再度成為第一實例
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var guard = SingleInstanceGuard.Acquire(UniqueName());
        guard.Dispose();
        guard.Dispose(); // 重複釋放不應拋例外
    }
}
