using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// 字幕檔自帶時間＋說話人之解析（epic #178 增量6′-B「時間 pivot」定案——直接載入、不對齊、不 Whisper）：
/// SubtitleParser.Parse 解 VTT/SRT 之時間軸與 &lt;v&gt; 說話人；ExtractInlineSpeakers 補抽「NAME:」行首前綴說話人（保守、避免把「Well: …」誤判）。
/// </summary>
public class InlineSpeakerTests
{
    [Fact]
    public void ExtractInlineSpeakers_PrefixForms_ExtractedTimeUntouched()
    {
        var cues = new[]
        {
            new SubtitleCue("Ryder: Ready, Marshall?", 10.0),
            new SubtitleCue("Cap'n Turbot: Cool cavern!", 20.0),
            new SubtitleCue("CHASE: Come on, pups!", 30.0),
        };
        var r = SubtitleParser.ExtractInlineSpeakers(cues);
        Assert.Equal("Ryder", r[0].Speaker); Assert.Equal("Ready, Marshall?", r[0].Text);
        Assert.Equal("Cap'n Turbot", r[1].Speaker); Assert.Equal("Cool cavern!", r[1].Text);
        Assert.Equal("CHASE", r[2].Speaker); Assert.Equal("Come on, pups!", r[2].Text);
        Assert.Equal(10.0, r[0].StartSec); // 時間完全不動
    }

    [Fact]
    public void ExtractInlineSpeakers_KeepsExistingSpeaker_AndText()
    {
        var cues = new[] { new SubtitleCue("Marshall: Oops.", 5.0, "Rubble") }; // 已由 <v> 取得說話人
        var r = SubtitleParser.ExtractInlineSpeakers(cues);
        Assert.Equal("Rubble", r[0].Speaker);        // 不覆寫既有說話人
        Assert.Equal("Marshall: Oops.", r[0].Text);  // 也不改文字
    }

    [Theory]
    [InlineData("Well, I think: yes.")]                              // 逗號→前綴非名字
    [InlineData("http://example.com is a site")]                    // 小寫開頭
    [InlineData("This is a really long sentence that has: a colon")] // 前綴 >3 詞、過長
    [InlineData("No colon here at all")]
    public void ExtractInlineSpeakers_NonSpeakerColons_Unchanged(string text)
    {
        var cues = new[] { new SubtitleCue(text, 1.0) };
        var r = SubtitleParser.ExtractInlineSpeakers(cues);
        Assert.Null(r[0].Speaker);
        Assert.Equal(text, r[0].Text);
    }

    [Fact]
    public void Parse_VttWithVoiceTagsAndTimes_YieldsTimesAndSpeakers()
    {
        var vtt =
            "WEBVTT\n\n" +
            "00:00:10.000 --> 00:00:12.000\n<v Ryder>Ready, Marshall?\n\n" +
            "00:00:20.500 --> 00:00:23.000\n<v Marshall>I don't think I can do it.\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(10.0, cues[0].StartSec);
        Assert.Equal("Ryder", cues[0].Speaker);
        Assert.Equal(20.5, cues[1].StartSec);
        Assert.Equal("Marshall", cues[1].Speaker);
    }

    [Fact]
    public void ParseTimedTranscript_FandomStyleTable_TimesAndSpeakers()
    {
        // fandom 逐字稿頁式：HTML 表格,每列「HH:MM:SS ＋ Speaker: ＋ 台詞」（含冒號前後空白、換行）。
        var html =
            "<table>" +
            "<tr><td>00:00:47</td><td>Cap'n Turbot:</td><td>(Sighing)</td></tr>" +
            "<tr><td>00:00:49</td><td>Cap'n\nTurbot :</td><td>Come on, my beautiful blue-footed booby bird!</td></tr>" +
            "<tr><td>00:01:22</td><td>Ryder:</td><td>Ready, Marshall?</td></tr>" +
            "</table>";
        var cues = SubtitleParser.ExtractInlineSpeakers(SubtitleParser.ParseTimedTranscript(html));
        Assert.Equal(3, cues.Count);
        Assert.Equal(47.0, cues[0].StartSec);
        Assert.Equal("Cap'n Turbot", cues[0].Speaker);
        Assert.Equal("(Sighing)", cues[0].Text);
        Assert.Equal(49.0, cues[1].StartSec);
        Assert.Equal("Cap'n Turbot", cues[1].Speaker);      // 容忍「Turbot :」冒號前空白
        Assert.Equal(82.0, cues[2].StartSec);               // 00:01:22 → 82 秒
        Assert.Equal("Ryder", cues[2].Speaker);
        // 時間遞增（逐字稿本即依時序）——不再亂序
        Assert.True(cues[0].StartSec < cues[1].StartSec && cues[1].StartSec < cues[2].StartSec);
    }

    [Fact]
    public void ParseTimedTranscript_NoTimestamps_Empty()
    {
        var html = "<html><body><p>Ryder: Ready?</p><p>Marshall: Okay.</p></body></html>";
        Assert.Empty(SubtitleParser.ParseTimedTranscript(html));
    }

    [Fact]
    public void NormalizeOrder_OutOfOrderTimes_SortedAscending()
    {
        // 實測 fandom 逐字稿頁出現回退段（875→730）：場景倒敘/片尾曲另計時致句序時間不遞增。
        // PauseDecider 要求遞增,否則「選 Ryder」在回退段整批被瞬間掃過（USR「沒每次停」病根）。
        var cues = new[]
        {
            new SubtitleCue("a", 850.0, "Ryder"),
            new SubtitleCue("b", 875.0, "Rocky"),
            new SubtitleCue("c", 730.0, "Ryder"),   // 回退：730 < 875
            new SubtitleCue("d", 740.0, "Ryder"),
        };
        var r = SubtitleParser.NormalizeOrder(cues);
        Assert.Equal(new[] { 730.0, 740.0, 850.0, 875.0 }, r.Select(c => c.StartSec!.Value));
        Assert.Equal(new[] { "c", "d", "a", "b" }, r.Select(c => c.Text));
    }

    [Fact]
    public void NormalizeOrder_AlreadyAscending_Idempotent()
    {
        var cues = new[]
        {
            new SubtitleCue("a", 10.0), new SubtitleCue("b", 20.0), new SubtitleCue("c", 30.0),
        };
        var r = SubtitleParser.NormalizeOrder(cues);
        Assert.Equal(new[] { "a", "b", "c" }, r.Select(c => c.Text)); // 不動
    }

    [Fact]
    public void NormalizeOrder_UntimedCue_CarriesForwardBesidePrecedingTimed()
    {
        // 未定時句（null）承接前一已定時句時間、隨其相鄰——不因無時間被推到頭尾。
        var cues = new[]
        {
            new SubtitleCue("a", 10.0),
            new SubtitleCue("note", null),          // 承接 10 → 黏在 a 之後
            new SubtitleCue("b", 5.0),              // 回退到 5 → 排到最前
        };
        var r = SubtitleParser.NormalizeOrder(cues);
        Assert.Equal(new[] { "b", "a", "note" }, r.Select(c => c.Text));
        Assert.Null(r[2].StartSec); // note 仍為未定時（僅排序、不竄改時間）
    }

    [Fact]
    public void NormalizeOrder_EqualTimes_StableOriginalOrder()
    {
        var cues = new[]
        {
            new SubtitleCue("first", 12.0), new SubtitleCue("second", 12.0), new SubtitleCue("third", 12.0),
        };
        var r = SubtitleParser.NormalizeOrder(cues);
        Assert.Equal(new[] { "first", "second", "third" }, r.Select(c => c.Text)); // 同秒保留原順序
    }
}
