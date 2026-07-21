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

    // --- ReadableOnLight（#201：過淺主題色壓暗至白底可讀，供說話人清單小字字型色） ---

    [Fact]
    public void ReadableOnLight_TooLightColor_DarkenedToMeetContrast()
    {
        // 亮黃 #FDD835 對白底對比 ~1.4:1，遠低於門檻→須壓暗
        var readable = ColorMath.ReadableOnLight("#FDD835");
        Assert.True(ColorMath.TryParseHex(readable, out var r, out var g, out var b));
        var contrast = ColorMath.ContrastRatio(ColorMath.RelativeLuminance(r, g, b), 1.0);
        Assert.True(contrast >= 3.0 - 0.05, $"壓暗後對白底對比應達門檻 3.0，實得 {contrast:0.00}");
        Assert.NotEqual("#FDD835", readable); // 已壓暗、非原亮黃
    }

    [Fact]
    public void ReadableOnLight_AlreadyReadable_Unchanged()
    {
        // 深洋紅 #D81B60 對白底對比 ~5:1，已達標→原樣返回（大寫正規化）
        Assert.Equal("#D81B60", ColorMath.ReadableOnLight("#D81B60"));
    }

    [Fact]
    public void ReadableOnLight_PreservesHue_WhenDarkening()
    {
        var readable = ColorMath.ReadableOnLight("#FDD835"); // 黃（色相約 45–55°）
        Assert.True(ColorMath.TryParseHex(readable, out var r, out var g, out var b));
        ColorMath.RgbToHsl(r, g, b, out var h, out _, out _);
        Assert.InRange(h, 40.0, 65.0); // 壓暗保色相，仍是黃
    }

    [Fact]
    public void ReadableOnLight_InvalidHex_Empty()
    {
        Assert.Equal("", ColorMath.ReadableOnLight("nope"));
        Assert.Equal("", ColorMath.ReadableOnLight(null));
    }
}
