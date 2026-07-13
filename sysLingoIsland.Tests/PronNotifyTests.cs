using LingoIsland.Present;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 發音練習通知文案組裝（[modPresent模組] 發音回饋通知契約，spec#10／#101）：純函式——標題含目標句、
/// 內文含分數/門檻/過不過＋建議或失敗訊息、門檻邊界。不實際彈通知。
/// </summary>
public class PronNotifyTests
{
    [Fact]
    public void Result_Pass_TitleHasTarget_BodyHasScoreThresholdCheckAndNote()
    {
        var (title, body) = PronNotify.Result("Choose your weapon wisely", 88, 80, "great job");
        Assert.Contains("Choose your weapon wisely", title); // 標題明載在練哪一句
        Assert.Contains("88 / 80", body);
        Assert.Contains("passed", body);
        Assert.Contains("great job", body);                  // AI 建議在內文
    }

    [Fact]
    public void Result_BelowThreshold_BodyHasTryAgain_NoPassed()
    {
        var (_, body) = PronNotify.Result("hello", 62, 80, "");
        Assert.Contains("62 / 80", body);
        Assert.Contains("try again", body);
        Assert.DoesNotContain("passed", body);
    }

    [Fact]
    public void Result_ScoreEqualsThreshold_Passes()
    {
        var (_, body) = PronNotify.Result("hi", 80, 80, "");
        Assert.Contains("passed", body); // 邊界：== 門檻即通過
    }

    [Fact]
    public void Result_ZeroScoreNoSpeechNote_ShownAsTryAgainWithNote()
    {
        var (_, body) = PronNotify.Result("say this", 0, 80, "未偵測到朗讀");
        Assert.Contains("0 / 80", body);
        Assert.Contains("try again", body);
        Assert.Contains("未偵測到朗讀", body);
    }

    [Fact]
    public void Failure_TitleHasTarget_BodyIsMessage()
    {
        var (title, body) = PronNotify.Failure("Loading the next area", "Recording too short");
        Assert.Contains("Loading the next area", title);
        Assert.Equal("Recording too short", body);
    }

    [Fact]
    public void EmptyTarget_FallsBackToGenericTitle()
    {
        var (title, _) = PronNotify.Result("", 90, 80, "");
        Assert.Equal("Pronunciation practice", title);
        var (title2, _) = PronNotify.Failure("   ", "No microphone found");
        Assert.Equal("Pronunciation practice", title2);
    }

    [Theory]
    [InlineData("Scoring failed: 0xE8 device failed", "Scoring failed. Please try again.")]
    [InlineData("Pronunciation scoring failed after 2 retries: API transient error 503", "Network or scoring service is busy. Please try again.")]
    [InlineData("OPENAI_API_KEY environment variable is not set; cannot score pronunciation.", "Set your OpenAI key to score pronunciation")]
    [InlineData("API responded 401: invalid api key", "OpenAI key was rejected. Check your API key.")]
    [InlineData("API responded 400: model does not support input_audio", "Scoring model is not available. Check the pronunciation model setting.")]
    [InlineData("Failed to parse scoring response (malformed): missing score", "Scoring service returned an unreadable result. Please try again.")]
    [InlineData("No audio to score.", "No audio was recorded. Hold the mic and speak clearly.")]
    public void Failure_NormalizesTechnicalMessages(string raw, string expected)
    {
        var (_, body) = PronNotify.Failure("hello", raw);
        Assert.Equal(expected, body);
        Assert.DoesNotContain("0x", body);
        Assert.DoesNotContain("API responded", body);
    }
}
