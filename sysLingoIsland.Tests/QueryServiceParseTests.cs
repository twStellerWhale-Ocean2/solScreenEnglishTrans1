using System.Text.Json;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modQuery模組] 查詢契約之回應解析（QueryService.Parse，internal 供測試）：
/// 三欄齊備／空欄／缺欄／空內容／非 JSON 之降級行為（spec#3）。
/// </summary>
public class QueryServiceParseTests
{
    /// <summary>包成 OpenAI chat.completions 外層，content 為模型回傳之 JSON 字串。</summary>
    private static string WrapApi(string contentJson)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(contentJson) + "}}]}";

    [Fact]
    public void Parse_ValidThreeFields_ReturnsResult()
    {
        var api = WrapApi("{\"original\":\"Hello\",\"phonetic\":\"h\\u0259ˈlo\\u028a\",\"translation\":\"你好\"}");
        var r = QueryService.Parse(api);
        Assert.Equal("Hello", r.Original);
        Assert.Equal("你好", r.Translation);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void Parse_EmptyThreeFields_IsEmpty()
    {
        var api = WrapApi("{\"original\":\"\",\"phonetic\":\"\",\"translation\":\"\"}");
        var r = QueryService.Parse(api);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void Parse_MissingField_ThrowsQueryException()
    {
        var api = WrapApi("{\"original\":\"Hi\",\"translation\":\"嗨\"}"); // 缺 phonetic
        Assert.Throws<QueryException>(() => QueryService.Parse(api));
    }

    [Fact]
    public void Parse_EmptyContent_ThrowsQueryException()
    {
        var api = WrapApi("");
        Assert.Throws<QueryException>(() => QueryService.Parse(api));
    }

    [Fact]
    public void Parse_NonJsonApiResponse_ThrowsQueryException()
    {
        Assert.Throws<QueryException>(() => QueryService.Parse("not json at all"));
    }

    // ---- #55：智能配色 color 欄解析 ----

    [Fact]
    public void Parse_WithThemeHex_LightensForBackground()
    {
        // #189-checklist：AI 改回主題色 hex（非固定色名）→ 調淡為白底可讀之筆記底色
        var api = WrapApi("{\"original\":\"Attack!\",\"phonetic\":\"x\",\"translation\":\"攻擊！\",\"color\":\"#E53935\"}");
        var r = QueryService.Parse(api);
        Assert.StartsWith("#", r.SuggestedColor);
        Assert.Equal(7, r.SuggestedColor.Length);
        Assert.NotEqual("#E53935", r.SuggestedColor); // 已調淡（非原飽和色）
    }

    [Fact]
    public void Parse_NoColorField_SuggestedColorEmpty()
    {
        // 無配色規則之查詢（無 color 欄）→ 建議色空、回歸行為
        var api = WrapApi("{\"original\":\"Hi\",\"phonetic\":\"x\",\"translation\":\"嗨\"}");
        var r = QueryService.Parse(api);
        Assert.Equal("", r.SuggestedColor);
    }

    [Fact]
    public void Parse_EmptyOrUnknownColor_SuggestedColorEmpty()
    {
        var api = WrapApi("{\"original\":\"Hi\",\"phonetic\":\"x\",\"translation\":\"嗨\",\"color\":\"Crimson\"}");
        Assert.Equal("", QueryService.Parse(api).SuggestedColor); // 非盤上色名→不套色
    }
}
