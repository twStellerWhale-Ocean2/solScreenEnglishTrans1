using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>色彩數學（#189-checklist）：#RRGGBB 解析、RGB↔HSL 往返、調淡為白底可讀底色。</summary>
public class ColorMathTests
{
    [Theory]
    [InlineData("#E53935", true, 0xE5, 0x39, 0x35)]
    [InlineData("#ffffff", true, 0xFF, 0xFF, 0xFF)]
    [InlineData("E53935", false, 0, 0, 0)]   // 缺 #
    [InlineData("#E539", false, 0, 0, 0)]    // 長度不符
    [InlineData("", false, 0, 0, 0)]
    [InlineData(null, false, 0, 0, 0)]
    public void TryParseHex(string? hex, bool ok, int r, int g, int b)
    {
        Assert.Equal(ok, ColorMath.TryParseHex(hex, out var rr, out var gg, out var bb));
        if (ok) { Assert.Equal((byte)r, rr); Assert.Equal((byte)g, gg); Assert.Equal((byte)b, bb); }
    }

    [Fact]
    public void LightenForBackground_SaturatedToPaleReadable_KeepsHue()
    {
        var pale = ColorMath.LightenForBackground("#E53935"); // 飽和紅
        Assert.True(ColorMath.TryParseHex(pale, out var r, out var g, out var b));
        ColorMath.RgbToHsl(r, g, b, out _, out _, out var l);
        Assert.True(l >= 0.85, $"亮度應提高（白底可讀），實得 {l:0.00}"); // 高亮度＝淺底、可讀
        Assert.NotEqual("#E53935", pale); // 已調淡、非原飽和色
    }

    [Fact]
    public void LightenForBackground_InvalidHex_Empty()
    {
        Assert.Equal("", ColorMath.LightenForBackground("nope"));
        Assert.Equal("", ColorMath.LightenForBackground(null));
    }

    [Theory]
    [InlineData("#1E88E5")]
    [InlineData("#43A047")]
    [InlineData("#FDD835")]
    public void RgbHslRoundtrip_WithinTolerance(string hex)
    {
        Assert.True(ColorMath.TryParseHex(hex, out var r, out var g, out var b));
        ColorMath.RgbToHsl(r, g, b, out var h, out var s, out var l);
        ColorMath.HslToRgb(h, s, l, out var r2, out var g2, out var b2);
        Assert.True(System.Math.Abs(r - r2) <= 1 && System.Math.Abs(g - g2) <= 1 && System.Math.Abs(b - b2) <= 1);
    }
}
