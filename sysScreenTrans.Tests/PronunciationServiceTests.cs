using System.Text.Json;
using ScreenTrans.Query;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// 發音評分解析與門檻（[techItem發音評分]，spec#10）：Parse 分數鉗制/缺欄/非 JSON、門檻判定、
/// payload 結構（input_audio＋模型＋structured output）。不打真網路、不佔麥克風。
/// </summary>
public class PronunciationServiceTests
{
    /// <summary>模擬 OpenAI chat completions 回應：choices[0].message.content 為 structured JSON 字串。</summary>
    private static string Wrap(string contentJson)
        => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = contentJson } } } });

    [Fact]
    public void Parse_ValidScoreAndNote()
    {
        var r = PronunciationService.Parse(Wrap("{\"score\":82,\"note\":\"再清楚一點\"}"));
        Assert.Equal(82, r.Score);
        Assert.Equal("再清楚一點", r.Note);
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void Parse_ClampsScoreTo0To100(int raw, int expected)
    {
        Assert.Equal(expected, PronunciationService.Parse(Wrap("{\"score\":" + raw + ",\"note\":\"\"}")).Score);
    }

    [Fact]
    public void Parse_ScoreAsString_Parsed()
    {
        Assert.Equal(73, PronunciationService.Parse(Wrap("{\"score\":\"73\",\"note\":\"\"}")).Score);
    }

    [Fact]
    public void Parse_MissingScore_Throws()
    {
        Assert.Throws<QueryException>(() => PronunciationService.Parse(Wrap("{\"note\":\"x\"}")));
    }

    [Fact]
    public void Parse_NonJsonContent_Throws()
    {
        Assert.Throws<QueryException>(() => PronunciationService.Parse(Wrap("not json at all")));
    }

    [Fact]
    public void Parse_EmptyChoices_Throws()
    {
        Assert.Throws<QueryException>(() => PronunciationService.Parse("{\"choices\":[]}"));
    }

    [Theory]
    [InlineData(80, 80, true)]
    [InlineData(79, 80, false)]
    [InlineData(100, 80, true)]
    [InlineData(0, 0, true)]
    public void IsPass_ThresholdBoundary(int score, int threshold, bool pass)
    {
        Assert.Equal(pass, new PronunciationResult(score).IsPass(threshold));
    }

    [Fact]
    public void BuildPayload_HasAudioModelTargetAndSchema()
    {
        var svc = new PronunciationService("gpt-4o-mini-audio-preview", 15, 2);
        var json = JsonSerializer.Serialize(svc.BuildPayload("QUJD", "Hello world"));
        Assert.Contains("input_audio", json);
        Assert.Contains("QUJD", json);                    // base64 audio embedded
        Assert.Contains("gpt-4o-mini-audio-preview", json);
        Assert.Contains("Hello world", json);             // target text in prompt
        Assert.Contains("json_schema", json);             // structured output
        Assert.Contains("pronunciation_score", json);
    }

    [Fact]
    public void Ctor_BlankModel_FallsBackToDefault()
    {
        var svc = new PronunciationService("", 15);
        var json = JsonSerializer.Serialize(svc.BuildPayload("QQ==", "hi"));
        Assert.Contains(PronunciationService.DefaultModel, json);
    }
}
