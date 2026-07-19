using System.Text.Json;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕主線 pivot 純函式（<see cref="TranscriptAlign"/>，epic #178 增量5′〔字幕檔＋Whisper 對齊〕）：
/// 去 HTML（StripToPlainText）、組整理提示（BuildParsePrompt）＋解析逐句序列（ParseLines）、渲染聲音時間軸（RenderAudioTimeline）＋
/// 組對齊提示（BuildAlignPrompt）＋解析每句時間（ParseTimes，-1→null、長度校正、四捨五入）、組裝帶說話人＋時間 cue（Assemble，敘事序、null 時間）。
/// 皆以假 Responses JSON／字串餵測、不打真網路。
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

    private static string LinesJson(params object[] lines) => JsonSerializer.Serialize(new { lines });
    private static object Line(string speaker, string text) => new { speaker, text };
    private static string RefsJson(params object[] refs) => JsonSerializer.Serialize(new { refs });
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

    // ── BuildParsePrompt ─────────────────────────────────────────────────────

    [Fact]
    public void BuildParsePrompt_IncludesTranscript_KeysAndInstructions()
    {
        var p = TranscriptAlign.BuildParsePrompt("Ryder: To the Lookout!");
        Assert.Contains("Ryder: To the Lookout!", p); // 內文帶入
        Assert.Contains("lines", p);                  // 要求 JSON 鍵
        Assert.Contains("speaker", p);
        Assert.Contains("text", p);
        Assert.Contains("說話者", p);                 // 每句判斷說話者
        Assert.Contains("略過", p);                   // 略過雜訊
    }

    // ── ParseLines ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseLines_ParsesSpeakerAndText_EmptySpeakerBecomesNull()
    {
        var json = Api(LinesJson(Line("Ryder", "To the Lookout!"), Line("", "No speaker here")));
        var lines = TranscriptAlign.ParseLines(json);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Ryder", lines[0].Speaker);
        Assert.Equal("To the Lookout!", lines[0].Text);
        Assert.Null(lines[1].Speaker);                // 空說話人→null
        Assert.Equal("No speaker here", lines[1].Text);
    }

    [Fact]
    public void ParseLines_SkipsEmptyText()
    {
        var json = Api(LinesJson(Line("X", "   "), Line("Y", "kept")));
        var lines = TranscriptAlign.ParseLines(json);
        Assert.Single(lines);
        Assert.Equal("kept", lines[0].Text);
    }

    [Fact]
    public void ParseLines_ToleratesCodeFence()
    {
        var fenced = "```json\n" + LinesJson(Line("Ryder", "Hi")) + "\n```";
        var lines = TranscriptAlign.ParseLines(Api(fenced));
        Assert.Single(lines);
        Assert.Equal("Ryder", lines[0].Speaker);
    }

    [Fact]
    public void ParseLines_TrimsSpeakerAndText()
    {
        var json = Api(LinesJson(Line("  Ryder  ", "  Hi there  ")));
        var lines = TranscriptAlign.ParseLines(json);
        Assert.Equal("Ryder", lines[0].Speaker);
        Assert.Equal("Hi there", lines[0].Text);
    }

    [Fact]
    public void ParseLines_MissingLinesKey_ReturnsEmpty()
    {
        var json = Api(JsonSerializer.Serialize(new { other = 1 }));
        Assert.Empty(TranscriptAlign.ParseLines(json));
    }

    [Fact]
    public void ParseLines_NoOutputText_ReturnsEmpty()
    {
        var json = JsonSerializer.Serialize(new { output = Array.Empty<object>() });
        Assert.Empty(TranscriptAlign.ParseLines(json));
    }

    [Fact]
    public void ParseLines_MalformedInnerJson_ReturnsEmpty()
    {
        Assert.Empty(TranscriptAlign.ParseLines(Api("{lines: not-json")));
    }

    [Fact]
    public void ParseLines_DropsPureNonSpeechLines()
    {
        var json = Api(LinesJson(
            Line("Cap'n Turbot", "(Sighing)"),                 // 純音效→丟
            Line("Cap'n Turbot", "(Gasping) Bingo!"),          // 含台詞→留
            Line("Blue-footed booby bird", "(Bird squawking)"),// 純音效→丟
            Line("Ryder", "Ready, Marshall?")));               // 台詞→留
        var lines = TranscriptAlign.ParseLines(json);
        Assert.Equal(2, lines.Count);
        Assert.Equal("(Gasping) Bingo!", lines[0].Text);
        Assert.Equal("Ready, Marshall?", lines[1].Text);
    }

    // ── IsPureNonSpeech（精度修：純舞台指示／音效丟棄） ─────────────────────────

    [Theory]
    [InlineData("(Sighing)", true)]
    [InlineData("(Bird squawking)", true)]
    [InlineData("[music]", true)]
    [InlineData("(Applause) (Cheering)", true)]
    [InlineData("...", true)]
    [InlineData("   ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("(Gasping) Bingo!", false)]
    [InlineData("Ready, Marshall?", false)]
    [InlineData("Chase is on the case!", false)]
    [InlineData("(Ryder over comm) PAW Patrol, to the Lookout!", false)]
    public void IsPureNonSpeech_DetectsStageDirections(string? text, bool expected)
        => Assert.Equal(expected, TranscriptAlign.IsPureNonSpeech(text));

    // ── RenderAudioTimeline ──────────────────────────────────────────────────

    [Fact]
    public void UsableAudioSegments_FiltersNullTimeAndEmptyText()
    {
        var cues = new List<SubtitleCue>
        {
            new("timed", 2.0),
            new("no time", null),   // 未定時→濾掉
            new("   ", 5.0),        // 空文字→濾掉
            new("later", 9.0),
        };
        var segs = TranscriptAlign.UsableAudioSegments(cues);
        Assert.Equal(2, segs.Count);
        Assert.Equal("timed", segs[0].Text);
        Assert.Equal("later", segs[1].Text);
    }

    [Fact]
    public void RenderAudioTimeline_Numbers1Based_NoTimesShown()
    {
        var segs = new List<SubtitleCue> { new("come on", 49.8), new("bingo", 58.7) };
        var timeline = TranscriptAlign.RenderAudioTimeline(segs);
        Assert.Equal("[1] come on\n[2] bingo", timeline);
        Assert.DoesNotContain("49.8", timeline); // 精度修：刻意不顯示秒數，模型只挑編號、不估時間
    }

    // ── BuildAlignPrompt ─────────────────────────────────────────────────────

    [Fact]
    public void BuildAlignPrompt_IncludesNumberedTimeline_CountRefsAndRules()
    {
        var chunk = new List<TranscriptLine> { new("Ryder", "To the Lookout"), new("Chase", "On the case") };
        var p = TranscriptAlign.BuildAlignPrompt(chunk, "[1] to the lookout\n[2] on the case");
        Assert.Contains("[1] to the lookout", p);      // 已編號聲音段帶入
        Assert.Contains("2", p);                       // 恰 N 句
        Assert.Contains("-1", p);                      // 對不到回 -1
        Assert.Contains("單調", p);                    // 單調不遞減
        Assert.Contains("refs", p);                    // 要求的 JSON 鍵（改為段編號 refs）
        Assert.Contains("編號", p);                    // 精度修：挑編號而非估時間
        Assert.Contains("1. To the Lookout", p);       // 編號台詞（用 text）
        Assert.Contains("2. On the case", p);
    }

    // ── ParseRefs（段編號）＋ MapRefsToTimes（取 Whisper 精確時間，精度修） ─────

    [Fact]
    public void ParseRefs_ParsesIndices_NonPositiveBecomesNull()
    {
        var refs = TranscriptAlign.ParseRefs(Api(RefsJson(2, -1, 5)), 3);
        Assert.Equal(3, refs.Count);
        Assert.Equal(2, refs[0]);
        Assert.Null(refs[1]);       // -1＝對不到→null
        Assert.Equal(5, refs[2]);
    }

    [Fact]
    public void ParseRefs_ShorterThanExpected_PadsNull()
    {
        var refs = TranscriptAlign.ParseRefs(Api(RefsJson(2)), 3);
        Assert.Equal(3, refs.Count);
        Assert.Equal(2, refs[0]);
        Assert.Null(refs[1]);
        Assert.Null(refs[2]);
    }

    [Fact]
    public void ParseRefs_LongerThanExpected_Truncates()
    {
        var refs = TranscriptAlign.ParseRefs(Api(RefsJson(1, 2, 3, 4)), 2);
        Assert.Equal(2, refs.Count);
        Assert.Equal(1, refs[0]);
        Assert.Equal(2, refs[1]);
    }

    [Fact]
    public void ParseRefs_ZeroOrMalformedOrMissing_Null()
    {
        Assert.Null(TranscriptAlign.ParseRefs(Api(RefsJson(0)), 1)[0]); // 0＝非 1-based 有效編號→null

        var missing = TranscriptAlign.ParseRefs(Api(JsonSerializer.Serialize(new { other = 1 })), 2);
        Assert.Equal(2, missing.Count);
        Assert.All(missing, r => Assert.Null(r));

        var malformed = TranscriptAlign.ParseRefs(Api("{refs: oops"), 2);
        Assert.Equal(2, malformed.Count);
        Assert.All(malformed, r => Assert.Null(r));
    }

    [Fact]
    public void ParseRefs_ZeroExpected_ReturnsEmpty()
    {
        Assert.Empty(TranscriptAlign.ParseRefs(Api(RefsJson(1)), 0));
    }

    [Fact]
    public void MapRefsToTimes_UsesExactSegmentTime_OutOfRangeOrNull()
    {
        var segs = new List<SubtitleCue> { new("a", 49.8), new("b", 58.7), new("c", 60.0) };
        var refs = new int?[] { 1, null, 3, 9 }; // 9 越界
        var times = TranscriptAlign.MapRefsToTimes(refs, segs);
        Assert.Equal(49.8, times[0]);   // 精確取段時間、非估算（精度＝Whisper 本身）
        Assert.Null(times[1]);          // null→時間未知
        Assert.Equal(60.0, times[2]);
        Assert.Null(times[3]);          // 越界→null
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
        Assert.False(TranscriptAlign.IsTruncated(Api(LinesJson(Line("Ryder", "Hi")))));
    }

    [Fact]
    public void IsTruncated_MalformedOrEmpty_False()
    {
        Assert.False(TranscriptAlign.IsTruncated("{not json"));
        Assert.False(TranscriptAlign.IsTruncated(""));
        Assert.False(TranscriptAlign.IsTruncated(null));
    }

    // ── Assemble ─────────────────────────────────────────────────────────────

    [Fact]
    public void Assemble_ZipsSpeakerTextTime_InNarrativeOrder()
    {
        var lines = new List<TranscriptLine> { new("Ryder", "A"), new("Chase", "B"), new(null, "C") };
        var times = new double?[] { 1.0, null, 3.0 };
        var cues = TranscriptAlign.Assemble(lines, times);
        Assert.Equal(3, cues.Count);
        // 敘事序不重排：A, B, C 順序不變
        Assert.Equal("A", cues[0].Text); Assert.Equal(1.0, cues[0].StartSec); Assert.Equal("Ryder", cues[0].Speaker);
        Assert.Equal("B", cues[1].Text); Assert.Null(cues[1].StartSec); Assert.Equal("Chase", cues[1].Speaker); // 未對齊→時間 null、留原位
        Assert.Equal("C", cues[2].Text); Assert.Equal(3.0, cues[2].StartSec); Assert.Null(cues[2].Speaker);
    }

    [Fact]
    public void Assemble_TrimsSpeaker_EmptyBecomesNull()
    {
        var lines = new List<TranscriptLine> { new("  Ryder  ", "A"), new("   ", "B") };
        var cues = TranscriptAlign.Assemble(lines, new double?[] { 1.0, 2.0 });
        Assert.Equal("Ryder", cues[0].Speaker);
        Assert.Null(cues[1].Speaker); // 空白說話人→null
    }

    [Fact]
    public void Assemble_FewerTimesThanLines_ExtraLinesGetNullTime()
    {
        var lines = new List<TranscriptLine> { new("X", "A"), new("Y", "B"), new("Z", "C") };
        var cues = TranscriptAlign.Assemble(lines, new double?[] { 1.0 });
        Assert.Equal(3, cues.Count);
        Assert.Equal(1.0, cues[0].StartSec);
        Assert.Null(cues[1].StartSec);
        Assert.Null(cues[2].StartSec);
    }

    [Fact]
    public void Assemble_Empty_ReturnsEmpty()
    {
        Assert.Empty(TranscriptAlign.Assemble(Array.Empty<TranscriptLine>(), Array.Empty<double?>()));
    }
}
