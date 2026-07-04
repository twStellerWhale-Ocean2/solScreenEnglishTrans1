using System.Text.Json;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

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
}
