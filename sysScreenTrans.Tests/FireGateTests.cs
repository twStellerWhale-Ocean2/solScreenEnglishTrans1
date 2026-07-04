using ScreenTrans.Capture;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [modCapture模組] 觸發收斂閘（FireGate，Issue #32）：連續命中僅首次派工，
/// 直到 Release 後才允許下一次——避免共用滑鼠鍵情境下的觸發風暴。
/// </summary>
public class FireGateTests
{
    [Fact]
    public void TryArm_OnlyFirstArms_UntilReleased()
    {
        var g = new FireGate();
        Assert.True(g.TryArm());   // 首次派工
        Assert.False(g.TryArm());  // 待處理中，略過
        Assert.False(g.TryArm());
        g.Release();               // handler 執行
        Assert.True(g.TryArm());   // 可再派工
    }

    [Fact]
    public void Release_WhenIdle_KeepsNextArmable()
    {
        var g = new FireGate();
        g.Release();               // 閒置時 release 不致鎖死
        Assert.True(g.TryArm());
    }
}
