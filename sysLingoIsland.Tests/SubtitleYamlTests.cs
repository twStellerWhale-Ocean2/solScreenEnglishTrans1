using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕整檔 YAML 序列化（SubtitleYaml，#154；start-only #158）：
/// cue↔YAML 往返（speaker/start/text，無 end）、手寫解析、空白說話人＝null、空文字略過、依起點排序、malformed 擲 SubtitleException。
/// </summary>
public class SubtitleYamlTests
{
    // start-only：cue 無 end；C 之 end 參數為相容既有寫法保留、實際忽略
    private static SubtitleCue C(string text, double start, double end = 0, string? speaker = null) => new(text, start, speaker);

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
        Assert.Equal(cues, parsed); // record 值相等：文字/起點/說話人皆還原
    }

    [Fact]
    public void SerializeThenParse_TrickyText_RoundTrips()
    {
        // 字幕真實可能出現冒號空白／前導方括號／引號／井號——YamlDotNet 序列化須自動引號，往返仍還原
        var cues = new[]
        {
            C("Note: watch closely.", 1, 2, "Ryder"),
            C("[door creaks]", 2, 3, null),
            C("It's \"go\" time, team!", 3, 4, "Skye"),
            C("Line with: colon and #hash", 4, 5, "Rocky"),
        };
        var parsed = SubtitleYaml.Parse(SubtitleYaml.Serialize(cues));
        Assert.Equal(cues, parsed);
    }

    [Fact]
    public void Serialize_EmptyList_ReturnsEmptyString()
    {
        Assert.Equal("", SubtitleYaml.Serialize(System.Array.Empty<SubtitleCue>()));
    }

    [Fact]
    public void Serialize_ContainsCamelCaseKeys_NoEnd()
    {
        var yaml = SubtitleYaml.Serialize(new[] { C("hi there", 1.0, 2.0, "Zuma") });
        Assert.Contains("speaker: Zuma", yaml);
        Assert.Contains("text: hi there", yaml);
        Assert.Contains("start: 1", yaml);
        Assert.DoesNotContain("end:", yaml); // start-only：無 end 欄
    }

    [Fact]
    public void Parse_Handwritten_ProducesCues()
    {
        // 使用者可打 start-only；即使殘留 end 鍵亦忽略（IgnoreUnmatchedProperties）
        var yaml = "- speaker: Marshall\n  start: 3\n  text: I'm fired up!\n"
                 + "- speaker:\n  start: 6\n  end: 8\n  text: On the double.\n";
        var cues = SubtitleYaml.Parse(yaml);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Marshall", cues[0].Speaker);
        Assert.Equal("I'm fired up!", cues[0].Text);
        Assert.Equal(3.0, cues[0].StartSec, 3);
        Assert.Null(cues[1].Speaker);           // 空白 speaker → null
        Assert.Equal("On the double.", cues[1].Text);
    }

    [Fact]
    public void Parse_SortsByStartSec()
    {
        // 使用者於整檔 YAML 打亂順序 → 解析後仍依起點遞增（PauseDecider 假定）
        var yaml = "- start: 5\n  text: third\n"
                 + "- start: 1\n  text: first\n"
                 + "- start: 3\n  text: second\n";
        var cues = SubtitleYaml.Parse(yaml);
        Assert.Equal(new[] { "first", "second", "third" }, cues.Select(c => c.Text));
    }

    [Fact]
    public void Parse_EmptyText_Skipped()
    {
        var yaml = "- speaker: A\n  start: 1\n  text: ''\n"
                 + "- speaker: B\n  start: 2\n  text: real line\n";
        var cues = SubtitleYaml.Parse(yaml);
        Assert.Single(cues);
        Assert.Equal("real line", cues[0].Text);
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
