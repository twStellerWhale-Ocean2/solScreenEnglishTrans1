using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕整檔 YAML 序列化（SubtitleYaml，epic #145 增量5，#154）：
/// cue↔YAML 往返、手寫解析、空白說話人＝null、空文字略過、保底區間、malformed 擲 SubtitleException。
/// </summary>
public class SubtitleYamlTests
{
    private static SubtitleCue C(string text, double start, double end, string? speaker = null) => new(text, start, end, speaker);

    [Fact]
    public void SerializeThenParse_RoundTrips()
    {
        var cues = new[]
        {
            C("Paw Patrol, to the Lookout!", 12.5, 15.2, "Ryder"),
            C("Ready for action!", 15.5, 17.0, null),   // 未標示說話人
            C("Green means go.", 17.5, 19.0, "Rocky"),
        };
        var parsed = SubtitleYaml.Parse(SubtitleYaml.Serialize(cues));
        Assert.Equal(cues, parsed); // record 值相等：文字/起訖/說話人皆還原
    }

    [Fact]
    public void Serialize_EmptyList_ReturnsEmptyString()
    {
        Assert.Equal("", SubtitleYaml.Serialize(System.Array.Empty<SubtitleCue>()));
    }

    [Fact]
    public void Serialize_ContainsCamelCaseKeys()
    {
        var yaml = SubtitleYaml.Serialize(new[] { C("hi there", 1.0, 2.0, "Zuma") });
        Assert.Contains("speaker: Zuma", yaml);
        Assert.Contains("text: hi there", yaml);
        Assert.Contains("start: 1", yaml);
        Assert.Contains("end: 2", yaml);
    }

    [Fact]
    public void Parse_Handwritten_ProducesCues()
    {
        var yaml = "- speaker: Marshall\n  start: 3\n  end: 5.5\n  text: I'm fired up!\n"
                 + "- speaker:\n  start: 6\n  end: 8\n  text: On the double.\n";
        var cues = SubtitleYaml.Parse(yaml);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Marshall", cues[0].Speaker);
        Assert.Equal("I'm fired up!", cues[0].Text);
        Assert.Equal(3.0, cues[0].StartSec, 3);
        Assert.Equal(5.5, cues[0].EndSec, 3);
        Assert.Null(cues[1].Speaker);           // 空白 speaker → null
        Assert.Equal("On the double.", cues[1].Text);
    }

    [Fact]
    public void Parse_EmptyText_Skipped()
    {
        var yaml = "- speaker: A\n  start: 1\n  end: 2\n  text: ''\n"
                 + "- speaker: B\n  start: 2\n  end: 3\n  text: real line\n";
        var cues = SubtitleYaml.Parse(yaml);
        Assert.Single(cues);
        Assert.Equal("real line", cues[0].Text);
    }

    [Fact]
    public void Parse_EndBeforeStart_GetsShortNonZeroSpan()
    {
        var cues = SubtitleYaml.Parse("- start: 5\n  end: 4\n  text: oops\n");
        Assert.Single(cues);
        Assert.True(cues[0].EndSec > cues[0].StartSec);
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(SubtitleYaml.Parse(null));
        Assert.Empty(SubtitleYaml.Parse(""));
        Assert.Empty(SubtitleYaml.Parse("   \n  "));
    }

    [Fact]
    public void Parse_InvalidYaml_ThrowsSubtitleException()
    {
        // 未閉合單引號＝YAML 掃描器錯誤
        var ex = Assert.Throws<SubtitleException>(() => SubtitleYaml.Parse("- text: 'unterminated\n"));
        Assert.Contains("Invalid YAML", ex.Message);
    }

    [Fact]
    public void Parse_NonSequence_ThrowsSubtitleException()
    {
        // 純量／對映非 cue 清單 → 轉型失敗擲 SubtitleException（不當機）
        Assert.Throws<SubtitleException>(() => SubtitleYaml.Parse("just a scalar, not a list"));
    }
}
