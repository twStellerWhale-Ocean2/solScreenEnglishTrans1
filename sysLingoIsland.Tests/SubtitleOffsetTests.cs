using System.Collections.Generic;
using LingoIsland.Present;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 時間偏移（epic #178 增量6′-B「時間 pivot」）：VideoCapturePage.ParseOffsetSeconds 解析 MM:SS／SS（可帶前導 -／+）、
/// ShiftCues 整體平移全部字幕時間（未定時句 null 不動、不低於 0、斷句／說話人不變）。並驗證「字幕快→輸負→延後」之慣例。
/// </summary>
public class SubtitleOffsetTests
{
    [Theory]
    [InlineData("00:05", 5)]
    [InlineData("-00:05", -5)]
    [InlineData("1:30", 90)]
    [InlineData("-1:30", -90)]
    [InlineData("5", 5)]
    [InlineData("-7", -7)]
    [InlineData("+00:03", 3)]
    [InlineData("00:00", 0)]
    [InlineData(" -00:05 ", -5)]
    public void ParseOffsetSeconds_Valid(string input, double expected)
        => Assert.Equal(expected, VideoCapturePage.ParseOffsetSeconds(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData("1:2:3")]   // HH:MM:SS 以上不支援
    [InlineData("1:-3")]    // 負號只允許在最前
    public void ParseOffsetSeconds_Invalid_ReturnsNull(string? input)
        => Assert.Null(VideoCapturePage.ParseOffsetSeconds(input));

    [Fact]
    public void ShiftCues_ShiftsTimed_KeepsNull_ClampsAtZero_PreservesTextSpeaker()
    {
        var cues = new List<SubtitleCue>
        {
            new("A", 10.0, "Ryder"),
            new("B", null, "Marshall"),   // 未定時句
            new("C", 3.0),
        };
        var shifted = VideoCapturePage.ShiftCues(cues, 5.0);
        Assert.Equal(15.0, shifted[0].StartSec);
        Assert.Null(shifted[1].StartSec);            // null 不動
        Assert.Equal(8.0, shifted[2].StartSec);
        Assert.Equal("Ryder", shifted[0].Speaker);   // 說話人不變
        Assert.Equal("A", shifted[0].Text);          // 文字不變

        var back = VideoCapturePage.ShiftCues(cues, -100.0);
        Assert.Equal(0.0, back[0].StartSec);         // 不低於 0
        Assert.Equal(0.0, back[2].StartSec);
    }

    [Fact]
    public void Convention_SubtitleFast_NegativeInput_MovesLater()
    {
        // 慣例：字幕快5秒（顯示於發音前）→ 使用者輸 -00:05 → ApplyOffset 以 -entered 平移 → 時間 +5（延後、對上發音）。
        var cues = new List<SubtitleCue> { new("Come on", 45.0) };
        var entered = VideoCapturePage.ParseOffsetSeconds("-00:05");
        Assert.Equal(-5.0, entered);
        var shifted = VideoCapturePage.ShiftCues(cues, -entered!.Value); // ApplyOffset 內即 ShiftCues(_cues, -secs)
        Assert.Equal(50.0, shifted[0].StartSec);     // 45 → 50（延後 5 秒對上真實發音）
    }
}
