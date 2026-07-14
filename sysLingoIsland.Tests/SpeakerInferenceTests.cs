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

    /// <summary>把逐句說話人內容包成 OpenAI chat/completions 回應形狀。</summary>
    private static string Api(string innerContent) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = innerContent } } } });

    // ── BuildPrompt ──

    [Fact]
    public void BuildPrompt_NumbersLines_AndIncludesTitle()
    {
        var p = SpeakerInference.BuildPrompt(new[] { C("Hello there"), C("General Kenobi") }, "Star Wars clip");
        Assert.Contains("1. Hello there", p);
        Assert.Contains("2. General Kenobi", p);
        Assert.Contains("Star Wars clip", p);
        Assert.Contains("speakers", p);
    }

    [Fact]
    public void BuildPrompt_NoTitle_OmitsTitleLine()
    {
        var p = SpeakerInference.BuildPrompt(new[] { C("solo line") }, null);
        Assert.Contains("1. solo line", p);
        Assert.DoesNotContain("影片標題", p);
    }

    // ── ParseSpeakers ──

    [Fact]
    public void ParseSpeakers_ExtractsArray_EmptyStringToNull()
    {
        var speakers = SpeakerInference.ParseSpeakers(Api("{\"speakers\":[\"Ryder\",\"\",\"Rubble\"]}"));
        Assert.Equal(3, speakers.Count);
        Assert.Equal("Ryder", speakers[0]);
        Assert.Null(speakers[1]);      // 空字串→null（未知）
        Assert.Equal("Rubble", speakers[2]);
    }

    [Fact]
    public void ParseSpeakers_MissingSpeakersKey_ReturnsEmpty()
    {
        Assert.Empty(SpeakerInference.ParseSpeakers(Api("{\"nope\":1}")));
    }

    [Fact]
    public void ParseSpeakers_EmptyOrWhitespaceContent_ReturnsEmpty()
    {
        Assert.Empty(SpeakerInference.ParseSpeakers(Api("")));
        Assert.Empty(SpeakerInference.ParseSpeakers(Api("   ")));
    }

    [Fact]
    public void ParseSpeakers_MalformedContent_Throws()
    {
        // content 非 JSON → JsonDocument.Parse(content) 擲例外（由 OpenAiSpeakerEnricher 轉可讀失敗）
        Assert.ThrowsAny<System.Exception>(() => SpeakerInference.ParseSpeakers(Api("not json at all")));
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
}
