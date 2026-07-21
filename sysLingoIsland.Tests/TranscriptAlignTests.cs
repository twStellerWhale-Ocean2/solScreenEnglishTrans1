using System.Text.Json;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕主線純函式（<see cref="TranscriptAlign"/>，epic #178 增量6′-B「時間 pivot」定案）：
/// 去 HTML（StripToPlainText）、組抽取提示（BuildExtractPrompt）＋解析逐句「時間＋說話人＋台詞」（ParseExtractedCues）、
/// 彈性時間戳解析（ParseFlexibleTime）、偵測輸出被上限截斷（IsTruncated）。皆以假 Responses JSON／字串餵測、不打真網路。
/// </summary>
public class TranscriptAlignTests
{
    /// <summary>把模型輸出文字包成 OpenAI Responses API 回應形狀（message.output_text）。</summary>
    private static string Api(string outputText) =>
        JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "message", role = "assistant", content = new object[]
                    { new { type = "output_text", text = outputText } } },
            },
        });

    private static string CuesJson(params object[] cues) => JsonSerializer.Serialize(new { cues });
    private static object Cue(string time, string speaker, string text) => new { time, speaker, text };

    // ── ParseFlexibleTime（增量6′-B「時間 pivot」：AI 抽取之時間戳照抄後解析）────────────
    [Theory]
    [InlineData("00:00:47", 47)]
    [InlineData("00:01:22", 82)]
    [InlineData("1:22", 82)]          // MM:SS
    [InlineData("0:00:47", 47)]
    [InlineData("1:30:00", 5400)]
    [InlineData("00:00:05.5", 5.5)]
    [InlineData("(0:47)", 47)]        // 自雜字元中取時鐘樣式
    [InlineData("47", 47)]            // 純秒
    public void ParseFlexibleTime_Valid(string input, double expected)
        => Assert.Equal(expected, TranscriptAlign.ParseFlexibleTime(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no time here")]
    public void ParseFlexibleTime_Invalid_Null(string? input)
        => Assert.Null(TranscriptAlign.ParseFlexibleTime(input));

    // ── ParseExtractedCues（增量6′-B：AI 逐句抽「時間＋說話人＋台詞」之回應解析）──────────
    [Fact]
    public void ParseExtractedCues_TimeSpeakerText_Parsed_EmptiesBecomeNull()
    {
        var json = Api(CuesJson(
            Cue("00:00:47", "Cap'n Turbot", "(Sighing)"),
            Cue("00:01:22", "Ryder", "Ready, Marshall?"),
            Cue("", "", "No time nor speaker")));
        var cues = TranscriptAlign.ParseExtractedCues(json);
        Assert.Equal(3, cues.Count);
        Assert.Equal(47.0, cues[0].StartSec);
        Assert.Equal("Cap'n Turbot", cues[0].Speaker);
        Assert.Equal("(Sighing)", cues[0].Text);
        Assert.Equal(82.0, cues[1].StartSec);
        Assert.Null(cues[2].StartSec);      // 空 time → null（時間未知）
        Assert.Null(cues[2].Speaker);       // 空 speaker → null
        // 時間遞增（AI 照抄網頁原有時間戳,非估算/對齊）
        Assert.True(cues[0].StartSec < cues[1].StartSec);
    }

    [Fact]
    public void ParseExtractedCues_SkipsEmptyText_ToleratesFence()
    {
        var fenced = "```json\n" + CuesJson(Cue("0:05", "A", "Hi"), Cue("0:06", "B", "  ")) + "\n```";
        var cues = TranscriptAlign.ParseExtractedCues(Api(fenced));
        Assert.Single(cues);                // 空白 text 之句略過
        Assert.Equal("Hi", cues[0].Text);
    }

    // ── StripToPlainText ─────────────────────────────────────────────────────

    [Fact]
    public void StripToPlainText_RemovesTags_KeepsTextOnSeparateLines()
    {
        var html = "<html><body><p>Ryder: To the Lookout!</p><p>Chase: Chase is on the case!</p></body></html>";
        var text = TranscriptAlign.StripToPlainText(html);
        Assert.Contains("Ryder: To the Lookout!", text);
        Assert.Contains("Chase: Chase is on the case!", text);
        Assert.DoesNotContain("<p>", text);
        Assert.DoesNotContain("</body>", text);
        // 區塊級標籤→換行：兩句應分行
        Assert.Contains("\n", text);
    }

    [Fact]
    public void StripToPlainText_DecodesHtmlEntities()
    {
        var text = TranscriptAlign.StripToPlainText("<p>Tom &amp; Jerry &lt;3 &quot;hi&quot; &#39;yo&#39;</p>");
        Assert.Contains("Tom & Jerry <3 \"hi\" 'yo'", text);
    }

    [Fact]
    public void StripToPlainText_DropsScriptAndStyleBlocks()
    {
        var html = "<style>.nav{color:red}</style><script>var x=1;alert(x)</script><p>Real line</p>";
        var text = TranscriptAlign.StripToPlainText(html);
        Assert.Contains("Real line", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("alert", text);
    }

    [Fact]
    public void StripToPlainText_BrBecomesNewline_AndCollapsesInlineWhitespace()
    {
        var text = TranscriptAlign.StripToPlainText("A<br>B    C\t\tD");
        Assert.Contains("A\nB", text);
        Assert.Contains("B C D", text); // 行內多空白/tab 收合為單一空白
    }

    [Fact]
    public void StripToPlainText_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal("", TranscriptAlign.StripToPlainText(null));
        Assert.Equal("", TranscriptAlign.StripToPlainText("   \n\t "));
    }

    [Fact]
    public void StripToPlainText_PlainTextPassesThrough()
    {
        var text = TranscriptAlign.StripToPlainText("Ryder: Ready for action?\nRubble: On the double!");
        Assert.Contains("Ryder: Ready for action?", text);
        Assert.Contains("Rubble: On the double!", text);
    }

    // ── IsTruncated（審查修：偵測 Responses 輸出被上限截斷） ──────────────────

    [Fact]
    public void IsTruncated_StatusIncomplete_True()
    {
        var json = JsonSerializer.Serialize(new { status = "incomplete", output = Array.Empty<object>() });
        Assert.True(TranscriptAlign.IsTruncated(json));
    }

    [Fact]
    public void IsTruncated_IncompleteDetailsMaxOutputTokens_True()
    {
        var json = JsonSerializer.Serialize(new { status = "incomplete", incomplete_details = new { reason = "max_output_tokens" } });
        Assert.True(TranscriptAlign.IsTruncated(json));
    }

    [Fact]
    public void IsTruncated_StatusCompleted_False()
    {
        var json = JsonSerializer.Serialize(new { status = "completed", output = Array.Empty<object>() });
        Assert.False(TranscriptAlign.IsTruncated(json));
    }

    [Fact]
    public void IsTruncated_NoStatus_False()
    {
        Assert.False(TranscriptAlign.IsTruncated(Api(CuesJson(Cue("0:01", "Ryder", "Hi")))));
    }

    [Fact]
    public void IsTruncated_MalformedOrEmpty_False()
    {
        Assert.False(TranscriptAlign.IsTruncated("{not json"));
        Assert.False(TranscriptAlign.IsTruncated(""));
        Assert.False(TranscriptAlign.IsTruncated(null));
    }
}
