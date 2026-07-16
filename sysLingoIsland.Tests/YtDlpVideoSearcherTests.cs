using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] YouTube 搜尋結果解析（YtDlpVideoSearcher.ParseResults，#171）：
/// yt-dlp --dump-json NDJSON → 結果清單；略過空/進度/無 id/malformed 行；空標題退 id。
/// </summary>
public class YtDlpVideoSearcherTests
{
    [Fact]
    public void ParseResults_NdjsonToResults()
    {
        var nd = "{\"id\":\"abc12345678\",\"title\":\"Cats\"}\n{\"id\":\"xyz98765432\",\"title\":\"Dogs\"}\n";
        var r = YtDlpVideoSearcher.ParseResults(nd);
        Assert.Equal(2, r.Count);
        Assert.Equal("abc12345678", r[0].VideoId);
        Assert.Equal("Cats", r[0].Title);
        Assert.Equal("xyz98765432", r[1].VideoId);
        Assert.Equal("Dogs", r[1].Title);
    }

    [Fact]
    public void ParseResults_SkipsBlankProgressNoIdAndMalformed()
    {
        var nd = "\n[download] Downloading...\n{\"id\":\"good1234567\",\"title\":\"Ok\"}\n{\"title\":\"NoId\"}\nnot json\n{bad json";
        var r = YtDlpVideoSearcher.ParseResults(nd);
        Assert.Single(r);
        Assert.Equal("good1234567", r[0].VideoId);
    }

    [Fact]
    public void ParseResults_EmptyTitle_FallsBackToId()
    {
        var r = YtDlpVideoSearcher.ParseResults("{\"id\":\"id123456789\",\"title\":\"\"}");
        Assert.Single(r);
        Assert.Equal("id123456789", r[0].Title);
    }

    [Fact]
    public void ParseResults_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(YtDlpVideoSearcher.ParseResults(null));
        Assert.Empty(YtDlpVideoSearcher.ParseResults(""));
        Assert.Empty(YtDlpVideoSearcher.ParseResults("   \n  \n"));
    }

    [Fact]
    public void ParseResults_ParsesDuration_NullWhenAbsentOrZero()
    {
        var nd = "{\"id\":\"abc12345678\",\"title\":\"A\",\"duration\":309}\n"
               + "{\"id\":\"xyz98765432\",\"title\":\"B\",\"duration\":0}\n"     // 0（直播/未知）→ null
               + "{\"id\":\"def11111111\",\"title\":\"C\"}";                      // 缺 → null
        var r = YtDlpVideoSearcher.ParseResults(nd);
        Assert.Equal(3, r.Count);
        Assert.Equal(309, r[0].DurationSec);
        Assert.Null(r[1].DurationSec);
        Assert.Null(r[2].DurationSec);
    }

    [Fact]
    public void ParseResults_DurationFloat_Rounds()
    {
        var r = YtDlpVideoSearcher.ParseResults("{\"id\":\"id123456789\",\"title\":\"T\",\"duration\":128.7}");
        Assert.Equal(129, r[0].DurationSec);
    }
}
