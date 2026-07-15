using System.Text.Json;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組]「由逐字稿找影片」純函式（TranscriptVideoFind，#189 獲得頁重構）：
/// 組 web_search 提示、解析 Responses 回應為候選（缺欄/malformed/圍籬容錯）、自 URL 取影片 ID。
/// </summary>
public class TranscriptVideoFindTests
{
    /// <summary>把 JSON 文字包成 OpenAI Responses API 回應形狀（web_search_call ＋ message.output_text）。</summary>
    private static string WebApi(string outputText) =>
        JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "web_search_call", id = "ws_1", status = "completed" },
                new { type = "message", role = "assistant", content = new object[]
                    { new { type = "output_text", text = outputText } } },
            },
        });

    // ── BuildFindVideosPrompt ──

    [Fact]
    public void BuildFindVideosPrompt_InstructsWebSearch_IncludesTopicMaxTheme()
    {
        var p = TranscriptVideoFind.BuildFindVideosPrompt("PAW Patrol", 5, "Kids cartoons");
        Assert.Contains("上網搜尋", p);
        Assert.Contains("PAW Patrol", p);
        Assert.Contains("5", p);
        Assert.Contains("Kids cartoons", p);
        Assert.Contains("videos", p);     // 要求的 JSON 鍵
        Assert.Contains("youtube_url", p);
        Assert.Contains("source", p);
    }

    [Fact]
    public void BuildFindVideosPrompt_NoTheme_Omitted()
    {
        var p = TranscriptVideoFind.BuildFindVideosPrompt("cooking shows", 8);
        Assert.Contains("cooking shows", p);
        Assert.DoesNotContain("所屬主題", p);
    }

    // ── ExtractVideoId（internal） ──

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abc123DEF_-", "abc123DEF_-")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]                       // 裸 11 碼
    [InlineData("https://example.com/no-id-here", null)]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    public void ExtractVideoId_ParsesLinksAndBareIds(string input, string? expected)
        => Assert.Equal(expected, TranscriptVideoFind.ExtractVideoId(input));

    // ── ParseCandidates ──

    [Fact]
    public void ParseCandidates_ParsesTitleUrlSource_ExtractsId()
    {
        var text = "{\"videos\":[" +
            "{\"title\":\"PAW Patrol S1E1\",\"youtube_url\":\"https://youtu.be/dQw4w9WgXcQ\",\"source\":\"PAW Patrol Wiki\"}," +
            "{\"title\":\"Bluey — The Weekend\",\"youtube_url\":\"https://www.youtube.com/watch?v=abcdefghijk\",\"source\":\"Bluey transcripts\"}]}";
        var list = TranscriptVideoFind.ParseCandidates(WebApi(text));
        Assert.Equal(2, list.Count);
        Assert.Equal("PAW Patrol S1E1", list[0].Title);
        Assert.Equal("dQw4w9WgXcQ", list[0].VideoId);
        Assert.Equal("PAW Patrol Wiki", list[0].Source);
        Assert.Equal("abcdefghijk", list[1].VideoId);
    }

    [Fact]
    public void ParseCandidates_UnparseableUrl_VideoIdNull_TitleKept()
    {
        var text = "{\"videos\":[{\"title\":\"Some Show\",\"youtube_url\":\"\",\"source\":\"scriptsite\"}]}";
        var list = TranscriptVideoFind.ParseCandidates(WebApi(text));
        var c = Assert.Single(list);
        Assert.Equal("Some Show", c.Title);
        Assert.Null(c.VideoId);        // UI 再以標題定位
        Assert.Equal("scriptsite", c.Source);
    }

    [Fact]
    public void ParseCandidates_SkipsBlankTitles()
    {
        var text = "{\"videos\":[{\"title\":\"  \",\"youtube_url\":\"https://youtu.be/dQw4w9WgXcQ\",\"source\":\"x\"}," +
            "{\"title\":\"Keep Me\",\"youtube_url\":\"\",\"source\":\"y\"}]}";
        var list = TranscriptVideoFind.ParseCandidates(WebApi(text));
        Assert.Single(list);
        Assert.Equal("Keep Me", list[0].Title);
    }

    [Fact]
    public void ParseCandidates_TolerantToFencesAndProse()
    {
        var text = "Here you go:\n```json\n{\"videos\":[{\"title\":\"T\",\"youtube_url\":\"dQw4w9WgXcQ\",\"source\":\"s\"}]}\n```\nHope it helps!";
        var list = TranscriptVideoFind.ParseCandidates(WebApi(text));
        Assert.Single(list);
        Assert.Equal("dQw4w9WgXcQ", list[0].VideoId);
    }

    [Fact]
    public void ParseCandidates_EmptyArray_ReturnsEmpty()
        => Assert.Empty(TranscriptVideoFind.ParseCandidates(WebApi("{\"videos\":[]}")));

    [Fact]
    public void ParseCandidates_NoVideosKey_ReturnsEmpty()
        => Assert.Empty(TranscriptVideoFind.ParseCandidates(WebApi("{\"other\":1}")));

    [Fact]
    public void ParseCandidates_NoMessage_ReturnsEmpty()
    {
        var json = JsonSerializer.Serialize(new { output = new object[] { new { type = "web_search_call", id = "ws_1" } } });
        Assert.Empty(TranscriptVideoFind.ParseCandidates(json));
    }

    [Fact]
    public void ParseCandidates_OutputTextConvenienceField()
    {
        var json = JsonSerializer.Serialize(new
        {
            output_text = "{\"videos\":[{\"title\":\"X\",\"youtube_url\":\"dQw4w9WgXcQ\",\"source\":\"s\"}]}",
            output = System.Array.Empty<object>(),
        });
        var list = TranscriptVideoFind.ParseCandidates(json);
        Assert.Single(list);
        Assert.Equal("X", list[0].Title);
    }
}
