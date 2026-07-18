using System.Text.Json;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 說話人推斷純函式（SpeakerInference，epic #145 增量6，#156）：
/// 組提示、解析 OpenAI 回應（空→null、缺欄、malformed）、非破壞疊加（填補未標示/保留既有/界限安全）、計數。
/// </summary>
public class SpeakerInferenceTests
{
    private static SubtitleCue C(string text, string? speaker = null) => new(text, 0, speaker);

    // ── BuildFindTranscriptPrompt 主題／CleanSpeaker 雜訊清理 ──
    // #180 清理：純推斷之 BuildPrompt／ParseSpeakers 與死碼 BuildWebPrompt 已移除；
    // CleanSpeaker（保留）之雜訊清理改由保留的 web 解析路徑（ParseWebSpeakers）覆蓋。

    [Fact]
    public void BuildFindTranscriptPrompt_IncludesTheme_WhenProvided()
    {
        Assert.Contains("PAW Patrol", SpeakerInference.BuildFindTranscriptPrompt("T", retryHint: null, videoTheme: "PAW Patrol"));
    }

    [Fact]
    public void ParseWebSpeakers_CleansNoiseLabels()
    {
        // #品質修：音效[.../旁白(.../不確定?/過長 → null（未知）；正常名保留（CleanSpeaker，經保留的 web 解析路徑）
        var speakers = SpeakerInference.ParseWebSpeakers(
            WebApi("{\"speakers\":[\"[music]\",\"(applause)\",\"?\",\"Chase/Ryder (?)\",\"Ryder\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}"));
        Assert.Equal(6, speakers.Count);
        Assert.Null(speakers[0]);  // [music]
        Assert.Null(speakers[1]);  // (applause)
        Assert.Null(speakers[2]);  // ?
        Assert.Null(speakers[3]);  // Chase/Ryder (?)（含 ?）
        Assert.Equal("Ryder", speakers[4]);
        Assert.Null(speakers[5]);  // 過長（思考外漏）
    }

    // ── MergeSpeakers（非破壞疊加）──

    [Fact]
    public void MergeSpeakers_FillsBlanks_KeepsExisting()
    {
        var cues = new[] { C("a"), C("b", "Existing"), C("c") };
        var merged = SpeakerInference.MergeSpeakers(cues, new string?[] { "X", "Y", "Z" });
        Assert.Equal("X", merged[0].Speaker);          // 未標示→填補
        Assert.Equal("Existing", merged[1].Speaker);   // 既有具名→保留（非破壞）
        Assert.Equal("Z", merged[2].Speaker);
        Assert.Equal("a", merged[0].Text);             // 文字不動
    }

    [Fact]
    public void MergeSpeakers_ShorterSpeakerList_BoundsSafe()
    {
        var merged = SpeakerInference.MergeSpeakers(new[] { C("a"), C("b"), C("c") }, new string?[] { "X" });
        Assert.Equal("X", merged[0].Speaker);
        Assert.Null(merged[1].Speaker);
        Assert.Null(merged[2].Speaker);
    }

    [Fact]
    public void MergeSpeakers_LongerSpeakerList_ExtrasIgnored()
    {
        var merged = SpeakerInference.MergeSpeakers(new[] { C("a"), C("b") }, new string?[] { "X", "Y", "Z" });
        Assert.Equal(2, merged.Count);
        Assert.Equal("Y", merged[1].Speaker);
    }

    [Fact]
    public void MergeSpeakers_NullInferred_LeavesBlank()
    {
        var merged = SpeakerInference.MergeSpeakers(new[] { C("a") }, new string?[] { null });
        Assert.Null(merged[0].Speaker);
    }

    [Fact]
    public void CountNewlyLabeled_CountsOnlyBlankToNamed()
    {
        var before = new[] { C("a"), C("b", "E"), C("c") };
        var after = new[] { C("a", "X"), C("b", "E"), C("c") };
        Assert.Equal(1, SpeakerInference.CountNewlyLabeled(before, after)); // 僅 a：未標示→有值
    }

    // ── 網搜來源（增量6b）：ParseWebSpeakers（Responses API 形狀） ──

    /// <summary>把逐句說話人內容包成 OpenAI Responses API 回應形狀（web_search_call ＋ message.output_text）。</summary>
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

    [Fact]
    public void ParseWebSpeakers_FromMessageOutputText_EmptyToNull()
    {
        var speakers = SpeakerInference.ParseWebSpeakers(WebApi("{\"speakers\":[\"Ryder\",\"\",\"Chase\"]}"));
        Assert.Equal(3, speakers.Count);
        Assert.Equal("Ryder", speakers[0]);
        Assert.Null(speakers[1]);
        Assert.Equal("Chase", speakers[2]);
    }

    [Fact]
    public void ParseWebSpeakers_TolerantToFencesAndProse()
    {
        var text = "Here are the speakers I found:\n```json\n{\"speakers\":[\"Marshall\",\"Skye\"]}\n```\nHope that helps!";
        Assert.Equal(new[] { "Marshall", "Skye" }, SpeakerInference.ParseWebSpeakers(WebApi(text)));
    }

    [Fact]
    public void ParseWebSpeakers_OutputTextConvenienceField()
    {
        var json = JsonSerializer.Serialize(new { output_text = "{\"speakers\":[\"X\"]}", output = Array.Empty<object>() });
        Assert.Equal(new[] { "X" }, SpeakerInference.ParseWebSpeakers(json));
    }

    [Fact]
    public void ParseWebSpeakers_NoMessage_ReturnsEmpty()
    {
        var json = JsonSerializer.Serialize(new { output = new object[] { new { type = "web_search_call", id = "ws_1" } } });
        Assert.Empty(SpeakerInference.ParseWebSpeakers(json));
    }

    [Fact]
    public void ParseWebSpeakers_MalformedEnvelope_Throws()
    {
        Assert.ThrowsAny<System.Exception>(() => SpeakerInference.ParseWebSpeakers("not json at all"));
    }

    // ── ParseUsage（費用估算之 token 用量；chat 與 Responses 皆掛 root.usage） ──

    [Fact]
    public void ParseUsage_ChatShape()
    {
        var u = SpeakerInference.ParseUsage("{\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30,\"total_tokens\":150}}");
        Assert.NotNull(u);
        Assert.Equal(120, u!.InputTokens);
        Assert.Equal(30, u.OutputTokens);
        Assert.Equal(150, u.TotalTokens);
    }

    [Fact]
    public void ParseUsage_ResponsesShape()
    {
        var u = SpeakerInference.ParseUsage("{\"usage\":{\"input_tokens\":200,\"output_tokens\":40,\"total_tokens\":240}}");
        Assert.NotNull(u);
        Assert.Equal(200, u!.InputTokens);
        Assert.Equal(40, u.OutputTokens);
        Assert.Equal(240, u.TotalTokens);
    }

    [Fact]
    public void ParseUsage_MissingUsageOrMalformed_ReturnsNull()
    {
        Assert.Null(SpeakerInference.ParseUsage("{\"choices\":[]}"));
        Assert.Null(SpeakerInference.ParseUsage("not json"));
    }

    [Fact]
    public void ParseUsage_MissingTotal_ComputesFromParts()
    {
        var u = SpeakerInference.ParseUsage("{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}");
        Assert.Equal(15, u!.TotalTokens);
    }

    // ── 逐字稿管線（增量6b 重做）：find/align 提示與 ParseFindResult ──

    [Fact]
    public void BuildFindTranscriptPrompt_InstructsWebSearch_IncludesTitle()
    {
        var p = SpeakerInference.BuildFindTranscriptPrompt("PAW Patrol S1E1");
        Assert.Contains("上網搜尋", p);
        Assert.Contains("PAW Patrol S1E1", p);
        Assert.Contains("transcript", p);   // JSON 欄位
    }

    [Fact]
    public void BuildAlignPrompt_IncludesTranscriptAndNumberedLines()
    {
        var p = SpeakerInference.BuildAlignPrompt("Ryder: Hi\nChase: Yo", new[] { C("Hello there"), C("Bye") });
        Assert.Contains("Ryder: Hi", p);      // 逐字稿放進提示
        Assert.Contains("1. Hello there", p);
        Assert.Contains("2. Bye", p);
        Assert.Contains("speakers", p);
    }

    [Fact]
    public void ParseFindResult_FromMessageOutputText()
    {
        var inner = "{\"found\":true,\"source\":\"fandom wiki\",\"complete\":true,\"transcript\":\"Ryder: Paw Patrol!\\nChase: On it!\"}";
        var find = SpeakerInference.ParseFindResult(WebApi(inner));
        Assert.True(find.Found);
        Assert.True(find.Complete);
        Assert.Equal("fandom wiki", find.Source);
        Assert.Contains("Ryder:", find.Transcript);
    }

    [Fact]
    public void ParseFindResult_NotFound()
    {
        var find = SpeakerInference.ParseFindResult(WebApi("{\"found\":false,\"source\":\"\",\"complete\":false,\"transcript\":\"\"}"));
        Assert.False(find.Found);
    }

    [Fact]
    public void ParseFindResult_MalformedOrNoMessage_NotFound()
    {
        Assert.False(SpeakerInference.ParseFindResult(WebApi("no json at all")).Found);
        var noMsg = JsonSerializer.Serialize(new { output = new object[] { new { type = "web_search_call" } } });
        Assert.False(SpeakerInference.ParseFindResult(noMsg).Found);
    }
}
