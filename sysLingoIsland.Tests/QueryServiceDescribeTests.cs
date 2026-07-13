using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>[modQuery模組] 圖片解釋回應解析（QueryService.ExtractContent，Issue #36／spec#9）。</summary>
public class QueryServiceDescribeTests
{
    [Fact]
    public void ExtractContent_ReturnsTrimmedText()
    {
        var api = "{\"choices\":[{\"message\":{\"content\":\"  中世紀奇幻 RPG 畫面。 \"}}]}";
        Assert.Equal("中世紀奇幻 RPG 畫面。", QueryService.ExtractContent(api));
    }

    [Fact]
    public void ExtractContent_Empty_Throws()
    {
        var api = "{\"choices\":[{\"message\":{\"content\":\"   \"}}]}";
        Assert.Throws<QueryException>(() => QueryService.ExtractContent(api));
    }

    [Fact]
    public void ExtractContent_Malformed_Throws()
    {
        Assert.Throws<QueryException>(() => QueryService.ExtractContent("not json"));
    }

    // ---- #53：structured content 解析名稱＋描述 ----

    [Fact]
    public void ParseImageContext_ReadsNameAndDescription()
    {
        var c = QueryService.ParseImageContext("{\"name\":\"薩爾達傳說\",\"description\":\"開放世界動作冒險遊戲畫面。\"}");
        Assert.Equal("薩爾達傳說", c.Name);
        Assert.Equal("開放世界動作冒險遊戲畫面。", c.Description);
    }

    [Fact]
    public void ParseImageContext_EmptyName_KeptEmpty()
    {
        // 無法辨識作品時 name 空 → 不自動填名
        var c = QueryService.ParseImageContext("{\"name\":\"\",\"description\":\"一般英文對話畫面。\"}");
        Assert.Equal("", c.Name);
        Assert.Equal("一般英文對話畫面。", c.Description);
    }

    [Fact]
    public void ParseImageContext_NonJson_FallsBackToDescriptionOnly()
    {
        // 模型偶未遵循 schema（回純文字）→ 整段當描述、名稱留空（容錯不致命）
        var c = QueryService.ParseImageContext("這是一段純文字描述。");
        Assert.Equal("", c.Name);
        Assert.Equal("這是一段純文字描述。", c.Description);
    }

    [Fact]
    public void ParseImageContext_TrimsWhitespace()
    {
        var c = QueryService.ParseImageContext("{\"name\":\"  Halo  \",\"description\":\"  科幻射擊。 \"}");
        Assert.Equal("Halo", c.Name);
        Assert.Equal("科幻射擊。", c.Description);
    }
}
