using System.Text.Json;
using LingoIsland.Query;
using Xunit;

namespace LingoIsland.Tests;

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
    public void BuildPayload_HasAudioModelTargetAndStructuredOutput()
    {
        var svc = new PronunciationService("gpt-audio-1.5", 15, 2);
        var json = JsonSerializer.Serialize(svc.BuildPayload("QUJD", "Hello world"));
        Assert.Contains("input_audio", json);
        Assert.Contains("QUJD", json);                    // base64 audio embedded
        Assert.Contains("gpt-audio-1.5", json);
        Assert.Contains("Hello world", json);             // target text in prompt
        Assert.Contains("json_schema", json);
        Assert.Contains("response_format", json);
        Assert.Contains("pronunciation_score", json);
        Assert.Contains("additionalProperties", json);
    }

    [Fact]
    public void BuildPayload_FallbackCanOmitStructuredOutput()
    {
        var svc = new PronunciationService("gpt-audio-1.5", 15, 2);
        var json = JsonSerializer.Serialize(svc.BuildPayload("QUJD", "Hello world", structured: false));
        Assert.Contains("input_audio", json);
        Assert.DoesNotContain("json_schema", json);
        Assert.DoesNotContain("response_format", json);
    }

    [Fact]
    public void Parse_ToleratesMarkdownFences()
    {
        var r = PronunciationService.Parse(Wrap("```json\n{\"score\": 91, \"note\": \"great\"}\n```"));
        Assert.Equal(91, r.Score);
        Assert.Equal("great", r.Note);
    }

    [Fact]
    public void Parse_ExtractsJsonFromSurroundingText()
    {
        Assert.Equal(77, PronunciationService.Parse(Wrap("Here is your score: {\"score\":77,\"note\":\"ok\"} thanks")).Score);
    }

    [Fact]
    public void Parse_ContentArrayTextParts_Parsed()
    {
        var api = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = new object[] { new { type = "text", text = "{\"score\":68,\"note\":\"keep practicing\"}" } },
                    },
                },
            },
        });
        var r = PronunciationService.Parse(api);
        Assert.Equal(68, r.Score);
        Assert.Equal("keep practicing", r.Note);
    }

    [Fact]
    public void Parse_Refusal_ThrowsQueryException()
    {
        var api = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { refusal = "cannot comply", content = "" } } },
        });
        Assert.Throws<QueryException>(() => PronunciationService.Parse(api));
    }

    [Fact]
    public void Ctor_BlankModel_FallsBackToDefault()
    {
        var svc = new PronunciationService("", 15);
        var json = JsonSerializer.Serialize(svc.BuildPayload("QQ==", "hi"));
        Assert.Contains(PronunciationService.DefaultModel, json);
    }

    [Fact]
    public void BasePrompt_OnlyUsesZeroForClearNoSpeech()
    {
        var p = PronunciationService.BasePrompt;
        Assert.Contains("score=0", p);                        // 明確無朗讀→0
        Assert.Contains("未偵測到朗讀", p);                     // 無朗讀之 note
        Assert.Contains("只有在靜音", p);                       // 0 分條件必須收窄
        Assert.Contains("完全與目標句無關", p);                 // 無關音訊才算未朗讀
        Assert.Contains("不可給 0 分", p);                      // 確有朗讀嘗試時避免全 0
        Assert.Contains("兒童聲音", p);                         // 童聲不能被誤判為沒朗讀
        Assert.Contains("口音很重", p);                         // 口音不能被誤判為沒朗讀
        Assert.Contains("15–100 分", p);                        // 有朗讀嘗試須給非零區間
        Assert.Contains("避免過度嚴格", p);                     // 防止回到全 0 的提示形態
    }

    [Fact]
    public void BuildPayload_CarriesNoSpeechGuardInPrompt()
    {
        var svc = new PronunciationService("gpt-audio-1.5", 15, 2);
        var json = JsonSerializer.Serialize(svc.BuildPayload("QUJD", "Hello world"));
        // 提示中之無朗讀防呆隨 payload 送出（CJK 於序列化會被 \uXXXX 逸出，故以 ASCII 片段驗）
        Assert.Contains("score=0", json);
    }

    [Fact]
    public void Parse_ZeroScoreWithNoSpeechNote()
    {
        var r = PronunciationService.Parse(Wrap("{\"score\":0,\"note\":\"未偵測到朗讀\"}"));
        Assert.Equal(0, r.Score);
        Assert.Equal("未偵測到朗讀", r.Note);
        Assert.False(r.IsPass(80)); // 0 分不通過
    }

    [Theory]
    [InlineData("No speech was detected in the audio.")]
    [InlineData("The recording appears to be silent.")]
    [InlineData("未偵測到朗讀。")]
    public void Parse_NoSpeechFreeText_ReturnsZeroInsteadOfUnreadable(string content)
    {
        var r = PronunciationService.Parse(Wrap(content));
        Assert.Equal(0, r.Score);
        Assert.Equal("未偵測到朗讀", r.Note);
    }
}
