using ScreenTrans.Present;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>筆記卡外框色計算（Issue #118→#123：先拉飽和 ×1.6 再加深 ×0.62；[USR] 回饋「飽和度調高」）——
/// 純函式定本測試，供 intTest#47 位圖精確色計數鏡像同一公式。無彩（灰階）拉飽和無作用、僅加深。</summary>
public class NoteCardBrushTests
{
    [Theory]
    [InlineData(255, 255, 255, 158, 158, 158)] // 白卡（無彩）→ 灰框 #9E9E9E（僅加深、飽和無作用）
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(0xFB, 0xE4, 0xEC, 160, 137, 145)] // Pink #FBE4EC → 框 #A08991（比純加深更鮮）
    [InlineData(0xB4, 0xEB, 0xFF, 95, 150, 169)]  // Sky（#109 最深底）→ 框 #5F96A9（藍更飽和）
    public void BorderRgb_SaturateThenDarken(byte r, byte g, byte b, byte er, byte eg, byte eb)
    {
        Assert.Equal((er, eg, eb), NoteCardBrush.BorderRgb(r, g, b));
    }

    [Fact]
    public void BorderFactors_Pinned()
    {
        Assert.Equal(0.62, NoteCardBrush.DarkenFactor, 3);     // 明度係數（#123）
        Assert.Equal(1.6, NoteCardBrush.SaturationBoost, 3);   // 飽和度加乘（#123 回饋）
    }

    [Fact]
    public void BorderRgb_Greyscale_StaysNeutral()
    {
        var (r, g, b) = NoteCardBrush.BorderRgb(220, 220, 220); // 無彩：拉飽和無作用、三通道仍相等
        Assert.Equal(r, g);
        Assert.Equal(g, b);
    }

    [Theory]
    [InlineData(true, true)]   // 通過＋透明開 → 透明底
    [InlineData(true, false)]  // 通過＋透明關 → 素底（#123 可選）
    [InlineData(false, true)]  // 未過 → 素底
    public void For_PassedTransparentToggle(bool passed, bool passedTransparent)
    {
        var brush = NoteCardBrush.For("#FBE4EC", passed, passedTransparent);
        bool isTransparent = ReferenceEquals(brush, System.Windows.Media.Brushes.Transparent);
        Assert.Equal(passed && passedTransparent, isTransparent);
    }
}
