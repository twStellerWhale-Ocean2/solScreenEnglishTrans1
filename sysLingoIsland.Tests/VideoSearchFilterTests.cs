using System.Collections.Generic;
using System.Linq;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 搜尋結果長度過濾（VideoSearchFilter.ByLength，#184）：
/// short&lt;4m／medium 4–20m／long&gt;20m；未知片長於有篩時排除；取前 max 筆。
/// </summary>
public class VideoSearchFilterTests
{
    private static VideoSearchResult V(string id, int? dur) => new(id, id, dur);

    private static readonly IReadOnlyList<VideoSearchResult> Sample = new List<VideoSearchResult>
    {
        V("a", 60),      // 1m  短
        V("b", 239),     // ~4m 短（<240）
        V("c", 240),     // 4m  中（含界）
        V("d", 900),     // 15m 中
        V("e", 1200),    // 20m 中（含界）
        V("f", 1201),    // 20m01 長（>1200）
        V("g", 3600),    // 60m 長
        V("h", null),    // 未知
    };

    [Fact]
    public void Short_UnderFourMinutes()
    {
        var r = VideoSearchFilter.ByLength(Sample, "short", 50).Select(x => x.VideoId).ToList();
        Assert.Equal(new[] { "a", "b" }, r);
    }

    [Fact]
    public void Medium_FourToTwentyMinutes_Inclusive()
    {
        var r = VideoSearchFilter.ByLength(Sample, "medium", 50).Select(x => x.VideoId).ToList();
        Assert.Equal(new[] { "c", "d", "e" }, r);
    }

    [Fact]
    public void Long_OverTwentyMinutes()
    {
        var r = VideoSearchFilter.ByLength(Sample, "long", 50).Select(x => x.VideoId).ToList();
        Assert.Equal(new[] { "f", "g" }, r);
    }

    [Fact]
    public void UnknownDuration_ExcludedWhenFiltering_KeptWhenNot()
    {
        Assert.DoesNotContain("h", VideoSearchFilter.ByLength(Sample, "short", 50).Select(x => x.VideoId));
        Assert.DoesNotContain("h", VideoSearchFilter.ByLength(Sample, "medium", 50).Select(x => x.VideoId));
        Assert.DoesNotContain("h", VideoSearchFilter.ByLength(Sample, "long", 50).Select(x => x.VideoId));
        Assert.Contains("h", VideoSearchFilter.ByLength(Sample, "", 50).Select(x => x.VideoId)); // 不篩→保留
    }

    [Fact]
    public void NoFilter_ReturnsAll_UpToMax()
    {
        Assert.Equal(8, VideoSearchFilter.ByLength(Sample, null, 50).Count);
        Assert.Equal(8, VideoSearchFilter.ByLength(Sample, "any-unknown-key", 50).Count);
    }

    [Fact]
    public void Max_CapsCount()
    {
        Assert.Equal(3, VideoSearchFilter.ByLength(Sample, null, 3).Count);
        Assert.Empty(VideoSearchFilter.ByLength(Sample, null, 0));
    }
}
