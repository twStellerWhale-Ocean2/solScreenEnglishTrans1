using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>筆記底色盤單一來源（Issue #55）：色名↔hex 對照與 AI 建議色正規化容錯。</summary>
public class NoteColorsTests
{
    [Theory]
    [InlineData("Pink", "#FBE4EC")]
    [InlineData("Blue", "#E1EFFB")]
    [InlineData("  Green ", "#E4F5E9")] // 前後空白容錯
    [InlineData("Violet", "#EEDBFF")]   // #109 新五色
    [InlineData("Sky", "#B4EBFF")]
    [InlineData("Mint", "#B8EDDE")]
    [InlineData("Lime", "#D1EAC7")]
    [InlineData("Orange", "#FFD9B8")]
    public void HexOfName_KnownName_ReturnsHex(string name, string hex)
    {
        Assert.Equal(hex, NoteColors.HexOfName(name));
    }

    [Fact]
    public void Palette_TenColors_NamesAndHexesUnique() // #109：十色、名與 hex 皆不重複（防加色手滑）
    {
        Assert.Equal(10, NoteColors.Palette.Length);
        Assert.Equal(10, NoteColors.Palette.Select(p => p.Name).Distinct().Count());
        Assert.Equal(10, NoteColors.Palette.Select(p => p.Hex.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void NormalizeSuggested_NewColor_NameAndHex() // #109：AI 建議色認得新色名與新 hex
    {
        Assert.Equal("#B4EBFF", NoteColors.NormalizeSuggested("Sky"));
        Assert.Equal("#FFD9B8", NoteColors.NormalizeSuggested("#ffd9b8")); // 正典化
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
    public void NormalizeSuggested_PaletteHex_CanonicalCase()
    {
        // #109 §5：盤上 hex（大小寫不敏感）正規化為 Palette 正典寫法——下游字面比對（選單打勾）判準一致
        Assert.Equal("#E1EFFB", NoteColors.NormalizeSuggested("#e1effb"));
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
