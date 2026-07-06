using System.Drawing;
using System.Drawing.Imaging;
using ScreenTrans.Capture;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// 凍結畫格裁切／標記（Issue #90）——自快照裁切選區、越界裁切、點選標記；純點陣邏輯可單元測試。
/// </summary>
public class FrozenDesktopTests
{
    private static FrozenDesktop Make(int w, int h, int ox = 0, int oy = 0)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.CornflowerBlue); }
        return new FrozenDesktop(bmp, ox, oy);
    }

    [Fact]
    public void CropToResult_ReturnsSelectedRegionSize()
    {
        using var f = Make(200, 100);
        var r = f.CropToResult(10, 20, 50, 30);
        Assert.NotNull(r);
        Assert.Equal(50, r!.Width);
        Assert.Equal(30, r.Height);
        Assert.False(r.IsPointMode);
        Assert.NotEmpty(r.PngBytes);
    }

    [Fact]
    public void CropToResult_HonorsVirtualOrigin()
    {
        // 原點 (-100,-50)：physical 座標需減原點才是快照索引
        using var f = Make(200, 100, ox: -100, oy: -50);
        var r = f.CropToResult(-90, -40, 20, 10); // → 快照 (10,10) 起 20x10
        Assert.NotNull(r);
        Assert.Equal(20, r!.Width);
        Assert.Equal(10, r.Height);
    }

    [Fact]
    public void CropToResult_PartlyOutOfBounds_ClampsToIntersection()
    {
        using var f = Make(100, 100);
        var r = f.CropToResult(80, 80, 50, 50); // 超出右下 → 裁切至 20x20
        Assert.NotNull(r);
        Assert.Equal(20, r!.Width);
        Assert.Equal(20, r.Height);
    }

    [Theory]
    [InlineData(0, 0, 0, 10)]   // 寬 0
    [InlineData(0, 0, 10, 0)]   // 高 0
    [InlineData(500, 500, 10, 10)] // 全落界外
    public void CropToResult_InvalidOrOutOfBounds_ReturnsNull(int px, int py, int w, int h)
    {
        using var f = Make(100, 100);
        Assert.Null(f.CropToResult(px, py, w, h));
    }

    [Fact]
    public void MarkToResult_ReturnsFullSnapshot_InPointMode()
    {
        using var f = Make(200, 100);
        var r = f.MarkToResult(50, 50);
        Assert.Equal(200, r.Width);
        Assert.Equal(100, r.Height);
        Assert.True(r.IsPointMode);
        Assert.NotEmpty(r.PngBytes);
    }
}
