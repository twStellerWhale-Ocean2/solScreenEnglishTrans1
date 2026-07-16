using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 內嵌字幕可用性解析（YtDlpSubtitleFetcher.ParseEmbeddedAvailability，#177）：
/// yt-dlp <c>--print "%(subtitles)j"</c>／<c>"%(automatic_captions)j"</c> 兩段 JSON 物件 → 人工/自動英文字幕有無。
/// 只認英文語碼鍵（en、en-US、en-GB、en-orig…）；非英文/空/null/malformed→false（不擲例外，UI 逐列容錯）。
/// </summary>
public class YtDlpSubtitleFetcherTests
{
    [Fact]
    public void Both_ManualAndAuto_English()
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability(
            "{\"en\":[{\"ext\":\"vtt\"}]}", "{\"en\":[{\"ext\":\"json3\"}]}");
        Assert.True(info.HasManual);
        Assert.True(info.HasAuto);
    }

    [Fact]
    public void AutoOnly_WhenManualEmpty()
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability("{}", "{\"en\":[{\"ext\":\"json3\"}]}");
        Assert.False(info.HasManual);
        Assert.True(info.HasAuto);
    }

    [Fact]
    public void ManualOnly_WhenAutoEmpty()
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability("{\"en-US\":[{\"ext\":\"vtt\"}]}", "{}");
        Assert.True(info.HasManual);
        Assert.False(info.HasAuto);
    }

    [Fact]
    public void Neither_WhenBothEmpty()
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability("{}", "{}");
        Assert.False(info.HasManual);
        Assert.False(info.HasAuto);
    }

    [Theory]
    [InlineData("{\"en\":[]}")]
    [InlineData("{\"en-US\":[]}")]
    [InlineData("{\"en-GB\":[]}")]
    [InlineData("{\"en-orig\":[]}")]
    [InlineData("{\"fr\":[],\"en-CA\":[]}")]  // 混雜語言中含英文變體
    public void DetectsEnglishVariants(string subtitlesJson)
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability(subtitlesJson, "{}");
        Assert.True(info.HasManual);
    }

    [Theory]
    [InlineData("{\"es\":[{\"ext\":\"vtt\"}]}")]      // 純西語
    [InlineData("{\"zh-Hant\":[],\"ja\":[]}")]        // 中日
    [InlineData("{\"enq\":[]}")]                      // 不是英文語碼（en 後接非分隔字元）
    public void NonEnglish_NotDetected(string subtitlesJson)
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability(subtitlesJson, "{}");
        Assert.False(info.HasManual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]        // yt-dlp 對缺值可能印 null
    [InlineData("not json")]
    [InlineData("[1,2,3]")]     // 非物件
    public void EmptyOrMalformed_False_NoThrow(string json)
    {
        var info = YtDlpSubtitleFetcher.ParseEmbeddedAvailability(json, json);
        Assert.False(info.HasManual);
        Assert.False(info.HasAuto);
    }
}
