using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

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
}
