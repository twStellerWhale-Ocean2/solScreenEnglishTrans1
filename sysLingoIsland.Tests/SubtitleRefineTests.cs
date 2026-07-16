using System.Collections.Generic;
using System.Linq;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕重分句純函式（#189 Row2）：解析 <see cref="SubtitleRefine.ParseSegmentsContent"/> 與
/// 以段序列建 cue（<see cref="SubtitleRefine.BuildCues"/>，**時間沿用原格、不變**）。HTTP 由 OpenAiRefiner 負責、不列入單元測試。
/// </summary>
public class SubtitleRefineTests
{
    private static IReadOnlyList<SubtitleCue> Base() => new[]
    {
        new SubtitleCue("The", 0.0),
        new SubtitleCue("pika.", 2.0),
        new SubtitleCue("It's", 5.0),
        new SubtitleCue("August.", 7.0),
    };

    // ── ParseSegmentsContent ──

    [Fact]
    public void ParseSegmentsContent_ParsesFields()
    {
        const string c = "{\"segments\":[{\"startIndex\":0,\"text\":\"The pika.\",\"speaker\":\"Narrator\"},{\"startIndex\":2,\"text\":\"It's August.\",\"speaker\":\"unknown\"}]}";
        var segs = SubtitleRefine.ParseSegmentsContent(c);
        Assert.Equal(2, segs.Count);
        Assert.Equal(0, segs[0].StartIndex);
        Assert.Equal("The pika.", segs[0].Text);
        Assert.Equal("Narrator", segs[0].Speaker);
        Assert.Equal(2, segs[1].StartIndex);
    }

    [Fact]
    public void ParseSegmentsContent_ToleratesCodeFence()
    {
        const string c = "```json\n{\"segments\":[{\"startIndex\":1,\"text\":\"pika.\",\"speaker\":\"x\"}]}\n```";
        var segs = SubtitleRefine.ParseSegmentsContent(c);
        Assert.Single(segs);
        Assert.Equal(1, segs[0].StartIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here")]
    [InlineData("{\"segments\": \"not array\"}")]
    public void ParseSegmentsContent_BlankOrMalformed_Empty(string c)
    {
        Assert.Empty(SubtitleRefine.ParseSegmentsContent(c));
    }

    // ── BuildCues ──

    [Fact]
    public void BuildCues_TakesTimeFromBaseStartIndex_MergesText()
    {
        var segs = new[]
        {
            new RefinedSegment(0, "The pika.", "Narrator"),   // 時間＝base[0]=0.0
            new RefinedSegment(2, "It's August.", "unknown"), // 時間＝base[2]=5.0
        };
        var cues = SubtitleRefine.BuildCues(Base(), segs);
        Assert.Equal(2, cues.Count);
        Assert.Equal(0.0, cues[0].StartSec);
        Assert.Equal("The pika.", cues[0].Text);
        Assert.Equal("Narrator", cues[0].Speaker);
        Assert.Equal(5.0, cues[1].StartSec);   // 沿用原格時間、不變
        Assert.Null(cues[1].Speaker);           // "unknown"→null（CleanSpeaker）
    }

    [Fact]
    public void BuildCues_SkipsOutOfRangeIndex_AndBlankText()
    {
        var segs = new[]
        {
            new RefinedSegment(9, "out of range", "x"), // 界外→略過
            new RefinedSegment(1, "   ", "x"),           // 空白→略過
            new RefinedSegment(0, "kept", "x"),
        };
        var cues = SubtitleRefine.BuildCues(Base(), segs);
        Assert.Single(cues);
        Assert.Equal("kept", cues[0].Text);
    }

    [Fact]
    public void BuildCues_SortsByStartTime()
    {
        var segs = new[]
        {
            new RefinedSegment(2, "later", "x"),   // 5.0
            new RefinedSegment(0, "earlier", "x"), // 0.0
        };
        var cues = SubtitleRefine.BuildCues(Base(), segs);
        Assert.Equal(new[] { "earlier", "later" }, cues.Select(c => c.Text).ToArray());
    }
}
