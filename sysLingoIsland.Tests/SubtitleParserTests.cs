using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕解析（SubtitleParser，spec#2；start-only #158）：VTT／SRT→逐句 start-only cue、
/// 時間解析、標籤/實體剝除、多行併行、去連續重複、空輸入降級；json3→TimedCue（內部含 end）→ 併句為 start-only。
/// </summary>
public class SubtitleParserTests
{
    [Fact]
    public void Parse_Vtt_ReturnsCuesWithStart()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nOnce upon a time\n\n"
                + "00:00:04.500 --> 00:00:07.000\nYou must venture beyond\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Once upon a time", cues[0].Text);
        Assert.Equal(1.0, cues[0].StartSec, 3);
        Assert.Equal("You must venture beyond", cues[1].Text);
        Assert.Equal(4.5, cues[1].StartSec, 3);
    }

    [Fact]
    public void Parse_Srt_CommaMillisAndIndexLines()
    {
        var srt = "1\n00:00:01,000 --> 00:00:03,000\nHello world\n\n"
                + "2\n00:00:03,000 --> 00:00:05,000\nGoodbye\n";
        var cues = SubtitleParser.Parse(srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello world", cues[0].Text);
        Assert.Equal("Goodbye", cues[1].Text);
    }

    [Fact]
    public void Parse_StripsTagsAndDecodesEntities()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<c>You &amp; me</c> <00:00:01.500><c> now</c>\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Single(cues);
        Assert.Equal("You & me now", cues[0].Text);
    }

    [Fact]
    public void Parse_MultiLineText_JoinedWithSpace()
    {
        var srt = "1\n00:00:01,000 --> 00:00:04,000\nLine one\nLine two\n";
        var cues = SubtitleParser.Parse(srt);
        Assert.Single(cues);
        Assert.Equal("Line one Line two", cues[0].Text);
    }

    [Fact]
    public void Parse_HoursInTimestamp()
    {
        var vtt = "WEBVTT\n\n01:02:03.000 --> 01:02:05.000\nDeep in\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Single(cues);
        Assert.Equal(3723.0, cues[0].StartSec, 3); // 1h 2m 3s
    }

    [Fact]
    public void Parse_DropsConsecutiveDuplicates()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nrolling\n\n"
                + "00:00:02.000 --> 00:00:03.000\nrolling\n\n"
                + "00:00:03.000 --> 00:00:04.000\nnew\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Equal(2, cues.Count);
        Assert.Equal("rolling", cues[0].Text);
        Assert.Equal("new", cues[1].Text);
    }

    [Fact]
    public void Parse_CollapsesRollingCaptions_KeepsFullestLine()
    {
        // YouTube 自動字幕逐字滾動：後句延伸前句 → 收斂為最完整那句（起始保留）；換句才另起
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nthe pups are\n\n"
                + "00:00:02.000 --> 00:00:03.000\nthe pups are ready\n\n"
                + "00:00:03.000 --> 00:00:04.000\nthe pups are ready to help\n\n"
                + "00:00:05.000 --> 00:00:06.000\nlet's roll\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Equal(2, cues.Count);
        Assert.Equal("the pups are ready to help", cues[0].Text);
        Assert.Equal(1.0, cues[0].StartSec, 3); // 起點保留
        Assert.Equal("let's roll", cues[1].Text);
    }

    [Fact]
    public void Parse_ZeroLengthOrReversedCue_Skipped()
    {
        var vtt = "WEBVTT\n\n00:00:02.000 --> 00:00:02.000\nzero\n\n00:00:05.000 --> 00:00:03.000\nreversed\n";
        Assert.Empty(SubtitleParser.Parse(vtt));
    }

    [Fact]
    public void Parse_NullEmptyOrNoTimestamps_ReturnsEmpty()
    {
        Assert.Empty(SubtitleParser.Parse(null));
        Assert.Empty(SubtitleParser.Parse(""));
        Assert.Empty(SubtitleParser.Parse("WEBVTT\n\njust some text, no timestamps\n"));
    }

    [Fact]
    public void ParseTime_HandlesBothSeparatorsAndOptionalHours()
    {
        Assert.Equal(63.5, SubtitleParser.ParseTime("01:03.500"), 3);   // MM:SS.mmm
        Assert.Equal(63.5, SubtitleParser.ParseTime("00:01:03,500"), 3); // HH:MM:SS,mmm
    }

    // ── json3（自動字幕改用之乾淨事件級格式，spec#2）→ TimedCue（內部含 end 供併句）──

    [Fact]
    public void ParseJson3Timed_EventsToCues_ConcatSegsAndTimes()
    {
        var json = """
        {"events":[
          {"tStartMs":1200,"dDurationMs":2000,"segs":[{"utf8":"Hello"},{"utf8":" world"}]},
          {"tStartMs":3500,"dDurationMs":1500,"segs":[{"utf8":"Goodbye"}]}
        ]}
        """;
        var cues = SubtitleParser.ParseJson3Timed(json);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello world", cues[0].Text);
        Assert.Equal(1.2, cues[0].StartSec, 3);
        Assert.Equal(3.2, cues[0].EndSec, 3);
        Assert.Equal("Goodbye", cues[1].Text);
        Assert.Equal(3.5, cues[1].StartSec, 3);
        Assert.Equal(5.0, cues[1].EndSec, 3);
    }

    [Fact]
    public void ParseJson3Timed_SkipsEmptyTextAndSegless()
    {
        var json = """
        {"events":[
          {"tStartMs":0,"dDurationMs":500,"segs":[{"utf8":"\n"}]},
          {"tStartMs":1000,"dDurationMs":1000,"segs":[{"utf8":"real"}]},
          {"tStartMs":2000,"dDurationMs":1000,"segs":[]}
        ]}
        """;
        var cues = SubtitleParser.ParseJson3Timed(json);
        Assert.Single(cues);
        Assert.Equal("real", cues[0].Text);
    }

    [Fact]
    public void ParseJson3Timed_DropsConsecutiveDuplicates()
    {
        var json = """
        {"events":[
          {"tStartMs":0,"dDurationMs":1000,"segs":[{"utf8":"same"}]},
          {"tStartMs":1000,"dDurationMs":1000,"segs":[{"utf8":"same"}]},
          {"tStartMs":2000,"dDurationMs":1000,"segs":[{"utf8":"next"}]}
        ]}
        """;
        var cues = SubtitleParser.ParseJson3Timed(json);
        Assert.Equal(2, cues.Count);
        Assert.Equal("same", cues[0].Text);
        Assert.Equal("next", cues[1].Text);
    }

    [Fact]
    public void ParseJson3Timed_ToleratesStringNumericFields()
    {
        var json = """{"events":[{"tStartMs":"2500","dDurationMs":"1500","segs":[{"utf8":"x"}]}]}""";
        var cues = SubtitleParser.ParseJson3Timed(json);
        Assert.Single(cues);
        Assert.Equal(2.5, cues[0].StartSec, 3);
        Assert.Equal(4.0, cues[0].EndSec, 3);
    }

    [Fact]
    public void ParseJson3Timed_ZeroDuration_GivesShortNonZeroSpan()
    {
        var cues = SubtitleParser.ParseJson3Timed("""{"events":[{"tStartMs":5000,"dDurationMs":0,"segs":[{"utf8":"blip"}]}]}""");
        Assert.Single(cues);
        Assert.True(cues[0].EndSec > cues[0].StartSec);
    }

    [Fact]
    public void ParseJson3Timed_NullEmptyOrMalformed_ReturnsEmpty()
    {
        Assert.Empty(SubtitleParser.ParseJson3Timed(null));
        Assert.Empty(SubtitleParser.ParseJson3Timed(""));
        Assert.Empty(SubtitleParser.ParseJson3Timed("not json"));
        Assert.Empty(SubtitleParser.ParseJson3Timed("{\"nope\":1}")); // 無 events
        Assert.Empty(SubtitleParser.ParseJson3Timed("[1,2,3]"));      // 非物件根
    }

    // ── CoalesceCues：json3 過細 TimedCue 併為句級（#143）→ 輸出 start-only SubtitleCue（#158）──

    private static TimedCue C(string text, double start, double end, string? speaker = null) => new(text, start, end, speaker);

    [Fact]
    public void CoalesceCues_MergesShortUntilSentenceEnd()
    {
        var cues = new[] { C("the storm blew", 1.0, 2.0), C("over almost all", 2.0, 3.0),
                           C("the bins.", 3.0, 4.0), C("Ready?", 4.0, 5.0) };
        var r = SubtitleParser.CoalesceCues(cues);
        Assert.Equal(2, r.Count);
        Assert.Equal("the storm blew over almost all the bins.", r[0].Text);
        Assert.Equal(1.0, r[0].StartSec, 3); // 保留首 cue 起點（start-only：無 end）
        Assert.Equal("Ready?", r[1].Text);
    }

    [Fact]
    public void CoalesceCues_BreaksOnTimeGap()
    {
        var r = SubtitleParser.CoalesceCues(new[] { C("hello there", 1.0, 2.0), C("friend", 5.0, 6.0) }); // gap 3.0 > 1.2
        Assert.Equal(2, r.Count);
        Assert.Equal("hello there", r[0].Text);
        Assert.Equal("friend", r[1].Text);
    }

    [Fact]
    public void CoalesceCues_BreaksBeforeNewSpeaker()
    {
        var r = SubtitleParser.CoalesceCues(new[] { C("I want to win", 1.0, 2.0), C(">> You have to beat me", 2.0, 3.0) });
        Assert.Equal(2, r.Count);
        Assert.Equal("I want to win", r[0].Text);
        Assert.StartsWith(">>", r[1].Text);
    }

    [Fact]
    public void CoalesceCues_BreaksAtMaxWords()
    {
        var cues = new[] { C("one two three four five", 0, 1), C("six seven eight nine ten", 1, 2),
                           C("eleven twelve thirteen fourteen", 2, 3), C("more", 3, 4) };
        var r = SubtitleParser.CoalesceCues(cues, maxWords: 14);
        Assert.Equal(2, r.Count); // 5+5+4=14 達上限 → "more" 另起
        Assert.Equal("more", r[1].Text);
    }

    [Fact]
    public void CoalesceCues_EmptyAndSingle()
    {
        Assert.Empty(SubtitleParser.CoalesceCues(System.Array.Empty<TimedCue>()));
        var one = SubtitleParser.CoalesceCues(new[] { C("solo", 1, 2) });
        Assert.Single(one);
        Assert.Equal("solo", one[0].Text);
    }

    // ── 說話人（VTT <v> 語音標記解析＋依說話人斷句，epic #145 增量5）──

    [Fact]
    public void Parse_ExtractsVoiceTagSpeaker()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n<v Ryder>Paw Patrol, to the Lookout!\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Single(cues);
        Assert.Equal("Ryder", cues[0].Speaker);
        Assert.Equal("Paw Patrol, to the Lookout!", cues[0].Text); // <v Ryder> 標記已剝除、不入文字
    }

    [Fact]
    public void Parse_VoiceTagWithClass_ExtractsSpeaker()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n<v.loud Rocky>Green means go!\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Single(cues);
        Assert.Equal("Rocky", cues[0].Speaker);
        Assert.Equal("Green means go!", cues[0].Text);
    }

    [Fact]
    public void Parse_NoVoiceTag_SpeakerNull()
    {
        var cues = SubtitleParser.Parse("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nJust a plain line\n");
        Assert.Single(cues);
        Assert.Null(cues[0].Speaker);
    }

    [Fact]
    public void CoalesceCues_BreaksOnNamedSpeakerChange()
    {
        // 兩具名說話人相鄰、無句末標點、間隔小、未達字數上限——僅「說話人變更」觸發斷句
        var r = SubtitleParser.CoalesceCues(new[] { C("green means", 1, 2, "Rocky"), C("go now", 2, 3, "Zuma") });
        Assert.Equal(2, r.Count);
        Assert.Equal("Rocky", r[0].Speaker);
        Assert.Equal("Zuma", r[1].Speaker);
    }

    [Fact]
    public void CoalesceCues_SameSpeaker_MergesAndKeepsSpeaker()
    {
        var r = SubtitleParser.CoalesceCues(new[] { C("green means", 1, 2, "Rocky"), C("go now", 2, 3, "Rocky") });
        Assert.Single(r);
        Assert.Equal("green means go now", r[0].Text);
        Assert.Equal("Rocky", r[0].Speaker);
    }

    [Fact]
    public void CoalesceCues_UnlabeledFollowedByNamed_Breaks()
    {
        // 未標示→具名＝說話人變更（斷句）；具名→未標示則視為延續（併入、沿用具名）
        var r = SubtitleParser.CoalesceCues(new[] { C("mystery line", 1, 2, null), C("hello", 2, 3, "Ryder") });
        Assert.Equal(2, r.Count);
        Assert.Null(r[0].Speaker);
        Assert.Equal("Ryder", r[1].Speaker);

        var r2 = SubtitleParser.CoalesceCues(new[] { C("green means", 1, 2, "Rocky"), C("go now", 2, 3, null) });
        Assert.Single(r2);
        Assert.Equal("Rocky", r2[0].Speaker);
    }
}
