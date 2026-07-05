using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>筆記底色盤單一來源（Issue #55）：色名↔hex 對照與 AI 建議色正規化容錯。</summary>
public class NoteColorsTests
{
    [Theory]
    [InlineData("Pink", "#FBE4EC")]
    [InlineData("Blue", "#E1EFFB")]
    [InlineData("  Green ", "#E4F5E9")] // 前後空白容錯
    public void HexOfName_KnownName_ReturnsHex(string name, string hex)
    {
        Assert.Equal(hex, NoteColors.HexOfName(name));
    }

    [Theory]
    [InlineData("Crimson")]
    [InlineData("")]
    [InlineData(null)]
    public void HexOfName_Unknown_ReturnsEmpty(string? name)
    {
        Assert.Equal("", NoteColors.HexOfName(name));
    }

    [Fact]
    public void NormalizeSuggested_ColorName_ToHex()
    {
        Assert.Equal("#FBE4EC", NoteColors.NormalizeSuggested("Pink"));
    }

    [Fact]
    public void NormalizeSuggested_PaletteHex_KeptAsIs_CaseInsensitive()
    {
        Assert.Equal("#e1effb", NoteColors.NormalizeSuggested("#e1effb")); // 盤上 hex（大小寫不敏感）保留原樣
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Purple")]    // 非盤上色名
    [InlineData("#123456")]  // 非盤上 hex
    public void NormalizeSuggested_UnknownOrEmpty_ReturnsEmpty(string? raw)
    {
        Assert.Equal("", NoteColors.NormalizeSuggested(raw));
    }
}
