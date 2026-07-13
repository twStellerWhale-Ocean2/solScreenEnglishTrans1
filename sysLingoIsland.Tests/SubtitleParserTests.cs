using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 字幕解析（SubtitleParser，spec#2）：VTT／SRT→逐句 cue、時間解析、
/// 標籤/實體剝除、多行併行、去連續重複、空輸入降級。
/// </summary>
public class SubtitleParserTests
{
    [Fact]
    public void Parse_Vtt_ReturnsCuesWithTimes()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nOnce upon a time\n\n"
                + "00:00:04.500 --> 00:00:07.000\nYou must venture beyond\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Once upon a time", cues[0].Text);
        Assert.Equal(1.0, cues[0].StartSec, 3);
        Assert.Equal(4.0, cues[0].EndSec, 3);
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
        Assert.Equal(3725.0, cues[0].EndSec, 3);
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
}
