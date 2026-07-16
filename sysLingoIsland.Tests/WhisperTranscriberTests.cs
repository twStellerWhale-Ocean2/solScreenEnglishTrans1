using System.Collections.Generic;
using System.Linq;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] Whisper 音訊轉錄之純函式（#187）：切塊規劃（<see cref="WhisperTranscriber.PlanChunks"/>）、
/// segment 偏移合併（<see cref="WhisperTranscriber.MergeSegments"/>）、verbose_json 解析（<see cref="WhisperTranscriber.ParseSegments"/>）
/// 與費用估算（<see cref="AiCost.EstimateWhisperUsd"/>）。行程/HTTP IO 不列入單元測試。
/// </summary>
public class WhisperTranscriberTests
{
    // ── PlanChunks ──

    [Fact]
    public void PlanChunks_ShortVideo_SingleChunk()
    {
        var r = WhisperTranscriber.PlanChunks(600, 1400);
        Assert.Single(r);
        Assert.Equal((0.0, 600.0), r[0]);
    }

    [Fact]
    public void PlanChunks_LongVideo_SplitsWithRemainder()
    {
        var r = WhisperTranscriber.PlanChunks(3000, 1400);
        Assert.Equal(3, r.Count);
        Assert.Equal((0.0, 1400.0), r[0]);
        Assert.Equal((1400.0, 1400.0), r[1]);
        Assert.Equal((2800.0, 200.0), r[2]); // 末塊補餘量
    }

    [Fact]
    public void PlanChunks_ExactMultiple_NoZeroTail()
    {
        var r = WhisperTranscriber.PlanChunks(2800, 1400);
        Assert.Equal(2, r.Count);
        Assert.Equal((1400.0, 1400.0), r[1]); // 剛好整除不產生 0 長度尾塊
    }

    [Theory]
    [InlineData(0, 1400)]
    [InlineData(-5, 1400)]
    [InlineData(600, 0)]
    public void PlanChunks_NonPositive_Empty(double total, double max)
    {
        Assert.Empty(WhisperTranscriber.PlanChunks(total, max));
    }

    // ── MergeSegments ──

    [Fact]
    public void MergeSegments_AddsChunkOffset_AndOrders()
    {
        var chunks = new List<(double, IReadOnlyList<WhisperTranscriber.WhisperSegment>)>
        {
            (0.0, new[] { new WhisperTranscriber.WhisperSegment(0, 2, "a"), new WhisperTranscriber.WhisperSegment(2, 4, "b") }),
            (1400.0, new[] { new WhisperTranscriber.WhisperSegment(0, 3, "c") }),
        };
        var cues = WhisperTranscriber.MergeSegments(chunks);
        Assert.Equal(new[] { "a", "b", "c" }, cues.Select(c => c.Text).ToArray());
        Assert.Equal(0.0, cues[0].StartSec);
        Assert.Equal(2.0, cues[1].StartSec);
        Assert.Equal(1400.0, cues[2].StartSec); // 第二塊 segment 加回 1400 偏移
    }

    [Fact]
    public void MergeSegments_SkipsBlank_AndTrims()
    {
        var chunks = new List<(double, IReadOnlyList<WhisperTranscriber.WhisperSegment>)>
        {
            (0.0, new[]
            {
                new WhisperTranscriber.WhisperSegment(0, 1, "   "),
                new WhisperTranscriber.WhisperSegment(1, 2, "  hi "),
            }),
        };
        var cues = WhisperTranscriber.MergeSegments(chunks);
        Assert.Single(cues);
        Assert.Equal("hi", cues[0].Text); // 空白句去除、前後空白修整
    }

    [Fact]
    public void MergeSegments_SortsByStart()
    {
        var chunks = new List<(double, IReadOnlyList<WhisperTranscriber.WhisperSegment>)>
        {
            (0.0, new[] { new WhisperTranscriber.WhisperSegment(5, 6, "late"), new WhisperTranscriber.WhisperSegment(1, 2, "early") }),
        };
        var cues = WhisperTranscriber.MergeSegments(chunks);
        Assert.Equal(new[] { "early", "late" }, cues.Select(c => c.Text).ToArray());
    }

    // ── ParseSegments ──

    [Fact]
    public void ParseSegments_ParsesStartEndText()
    {
        const string json = """
        {"task":"transcribe","language":"english","duration":4.2,
         "segments":[{"start":0.0,"end":2.1,"text":" Hello there."},{"start":2.1,"end":4.2,"text":" General Kenobi."}]}
        """;
        var segs = WhisperTranscriber.ParseSegments(json);
        Assert.Equal(2, segs.Count);
        Assert.Equal(0.0, segs[0].Start);
        Assert.Equal(2.1, segs[0].End);
        Assert.Equal(" Hello there.", segs[0].Text);
        Assert.Equal(2.1, segs[1].Start);
    }

    [Fact]
    public void ParseSegments_MissingSegments_Empty()
    {
        Assert.Empty(WhisperTranscriber.ParseSegments("""{"text":"no segments here"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"segments\": \"not an array\"}")]
    public void ParseSegments_BlankOrMalformed_Empty(string json)
    {
        Assert.Empty(WhisperTranscriber.ParseSegments(json));
    }

    // ── AiCost.EstimateWhisperUsd ──

    [Fact]
    public void EstimateWhisperUsd_OneMinute_IsPerMinuteRate()
    {
        Assert.Equal(AiCost.WhisperUsdPerMinute, AiCost.EstimateWhisperUsd(60), 6);
    }

    [Fact]
    public void EstimateWhisperUsd_TenMinutes_ScalesLinearly()
    {
        Assert.Equal(AiCost.WhisperUsdPerMinute * 10, AiCost.EstimateWhisperUsd(600), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void EstimateWhisperUsd_NonPositive_Zero(double sec)
    {
        Assert.Equal(0, AiCost.EstimateWhisperUsd(sec));
    }
}
