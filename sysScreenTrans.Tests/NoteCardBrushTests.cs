using ScreenTrans.Present;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>通過卡星紋底之暗色計算（Issue #111）：各通道 ×0.72、截斷取整——純函式定本測試，
/// 供 intTest#46 位圖精確色計數鏡像同一公式。</summary>
public class NoteCardBrushTests
{
    [Theory]
    [InlineData(255, 255, 255, 183, 183, 183)] // 白卡 → 灰星 #B7B7B7
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(0xFB, 0xE4, 0xEC, 180, 164, 169)] // Pink #FBE4EC → 星 #B4A4A9
    [InlineData(0xB4, 0xEB, 0xFF, 129, 169, 183)] // Sky（#109 最深底）
    public void Darken_MultiplicativeTruncation(byte r, byte g, byte b, byte er, byte eg, byte eb)
    {
        Assert.Equal((er, eg, eb), NoteCardBrush.Darken(r, g, b));
    }

    [Fact]
    public void DarkenFactor_Pinned()
    {
        Assert.Equal(0.72, NoteCardBrush.DarkenFactor, 3); // 定本（design ＜III.C.(C)＞）
    }
}
