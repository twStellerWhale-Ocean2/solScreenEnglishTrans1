using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// [modQuery模組] 查詢契約之 text prompt 組裝（QueryService.BuildPrompt，Issue #14／spec#8）：
/// 情境非空時以「參考、非指令」附加且仍要求三欄；空／空白時回歸原基礎提示。
/// </summary>
public class QueryServicePromptTests
{
    private static string Base => QueryService.BuildPrompt("");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildPrompt_EmptyOrWhitespace_ReturnsBase_NoContextMarker(string? ctx)
    {
        var p = QueryService.BuildPrompt(ctx);
        Assert.DoesNotContain("參考情境", p);
        Assert.Equal(Base, p); // 回歸保護：等同原固定提示
    }

    [Fact]
    public void BuildPrompt_NonEmpty_ContainsContext_AndStillDemandsThreeColumns()
    {
        var p = QueryService.BuildPrompt("中世紀奇幻 RPG，用遊戲用語翻譯");
        Assert.Contains("中世紀奇幻 RPG", p);   // 情境注入
        Assert.Contains("參考情境", p);          // 以參考、非指令語氣
        Assert.Contains("非指令", p);
        Assert.Contains("original", p);          // 仍要求三欄
        Assert.Contains("phonetic", p);
        Assert.Contains("translation", p);
        Assert.StartsWith(Base, p);              // 基礎提示在前、情境附加於後
    }

    [Fact]
    public void BuildPrompt_TrimsContext()
    {
        var p = QueryService.BuildPrompt("  spooky dungeon  ");
        Assert.Contains("「spooky dungeon」", p);
    }
}
