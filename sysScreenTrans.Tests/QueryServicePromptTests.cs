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

    // ---- #55：智能配色規則注入 ----

    [Fact]
    public void BuildPrompt_NoColorRules_NoColorInstruction()
    {
        var p = QueryService.BuildPrompt("some context");
        Assert.DoesNotContain("color", p);
        Assert.DoesNotContain("配色規則", p);
    }

    [Fact]
    public void BuildPrompt_ColorRules_AddsColorInstruction_AndKeepsThreeColumns()
    {
        var p = QueryService.BuildPrompt("", "戰士的台詞用粉紅、法師用粉藍");
        Assert.Contains("配色規則", p);
        Assert.Contains("戰士的台詞用粉紅", p);
        Assert.Contains("color", p);           // 要求 color 欄
        Assert.Contains("Pink", p);            // 可選色名清單（盤上英文色名）
        Assert.Contains("空字串", p);           // 無規則適用回空
        Assert.StartsWith(QueryService.BuildPrompt(""), p); // 基礎提示仍在前
    }

    [Fact]
    public void BuildPrompt_BothContextAndColorRules_ContainsBoth()
    {
        var p = QueryService.BuildPrompt("RPG", "boss 台詞用淺灰");
        Assert.Contains("參考情境", p);
        Assert.Contains("配色規則", p);
        Assert.Contains("boss 台詞用淺灰", p);
    }

    // ---- #54：雙擊自動判斷模式提示 ----

    [Fact]
    public void BuildPrompt_PointMode_UsesMarkerPrompt_StillThreeColumns()
    {
        var p = QueryService.BuildPrompt("", "", pointMode: true);
        Assert.Contains("標記", p);            // 以標記處為準
        Assert.Contains("最接近", p);
        Assert.Contains("original", p);        // 仍要求三欄
        Assert.Contains("phonetic", p);
        Assert.Contains("translation", p);
    }

    [Fact]
    public void BuildPrompt_PointMode_DiffersFromBase()
    {
        Assert.NotEqual(QueryService.BuildPrompt(""), QueryService.BuildPrompt("", "", pointMode: true));
    }

    [Fact]
    public void BuildPrompt_PointMode_WithContextAndColor_AppendsBoth()
    {
        var p = QueryService.BuildPrompt("科幻", "旁白用粉黃", pointMode: true);
        Assert.Contains("標記", p);
        Assert.Contains("參考情境", p);
        Assert.Contains("配色規則", p);
    }
}
