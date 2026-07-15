using System.Collections.Generic;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 導引播放到句暫停判定（PauseDecider，spec#2；start-only #158）：
/// 暫停於「下一句開始，或本句開始＋上限（防超長間隔乾等）」；顯示一句至下一句開始（無空窗）；不漏句、不重暫停、不早停。
/// </summary>
public class PauseDeciderTests
{
    // one(1)→two(3)：間隔 2，未達上限；two(3)→three(20)：間隔 17，超上限（8）→驗「開始＋上限」先停
    private static readonly List<SubtitleCue> Cues = new()
    {
        new SubtitleCue("one", 1.0),
        new SubtitleCue("two", 3.0),
        new SubtitleCue("three", 20.0),
    };

    [Fact]
    public void NextPause_PausesAtNextCueStart_NotBefore()
    {
        Assert.Equal(-1, PauseDecider.NextPause(2.0, Cues, -1));    // one 尚未到 two 之開始
        Assert.Equal(-1, PauseDecider.NextPause(2.999, Cues, -1));
        Assert.Equal(0, PauseDecider.NextPause(3.0, Cues, -1));     // 到 two 之開始→暫停 one
    }

    [Fact]
    public void NextPause_LongGap_PausesAtStartPlusCap_NotNextStart()
    {
        // two(3)→three(20) 間隔大；上限 8 → 於 3+8=11 先停，不等到 20
        Assert.Equal(-1, PauseDecider.NextPause(10.9, Cues, 0));
        Assert.Equal(1, PauseDecider.NextPause(11.0, Cues, 0));
    }

    [Fact]
    public void NextPause_LastCue_PausesAtStartPlusCap()
    {
        Assert.Equal(-1, PauseDecider.NextPause(27.0, Cues, 1));
        Assert.Equal(2, PauseDecider.NextPause(28.0, Cues, 1)); // three(20)+8=28
    }

    [Fact]
    public void NextPause_DoesNotRepauseSameCue()
    {
        Assert.Equal(-1, PauseDecider.NextPause(3.5, Cues, 0)); // 已暫停 one；two 暫停點 11 未到
        Assert.Equal(1, PauseDecider.NextPause(11.0, Cues, 0));
    }

    [Fact]
    public void NextPause_NoSkip_ReturnsImmediateNextEvenIfTimeJumps()
    {
        Assert.Equal(1, PauseDecider.NextPause(999.0, Cues, 0)); // 時間大跳仍回緊接的下一未暫停句
    }

    [Fact]
    public void NextPause_PastLastPaused_ReturnsMinusOne()
    {
        Assert.Equal(-1, PauseDecider.NextPause(999.0, Cues, 2));
    }

    [Fact]
    public void NextPause_EmptyCues_ReturnsMinusOne()
    {
        Assert.Equal(-1, PauseDecider.NextPause(5.0, new List<SubtitleCue>(), -1));
    }

    [Fact]
    public void NextPause_CustomMaxRun_CapsSooner()
    {
        // 自訂上限 2 秒：two(start3) 暫停點＝min(three.start 20, 3+2=5)=5
        Assert.Equal(-1, PauseDecider.NextPause(4.9, Cues, 0, maxRunSec: 2.0));
        Assert.Equal(1, PauseDecider.NextPause(5.0, Cues, 0, maxRunSec: 2.0));
    }

    // ── 口說時長地板（#字幕雙擊早停修正）：重疊字幕不把整句在說完前切掉 ──

    [Fact]
    public void NextPause_LongLineInShortSlot_ExtendsToSpokenFloor()
    {
        // 一句 10 詞（估 3.6s）但下一句只隔 2s 就開始（重疊字幕）→ 不於 2s 早停、延到 ~3.6s 播足整句
        var cues = new List<SubtitleCue>
        {
            new SubtitleCue("one two three four five six seven eight nine ten", 0.0),
            new SubtitleCue("next", 2.0),
        };
        Assert.Equal(-1, PauseDecider.NextPause(2.0, cues, -1)); // 到下一句起點(2s)仍不停——地板 3.6s 未到
        Assert.Equal(-1, PauseDecider.NextPause(3.5, cues, -1));
        Assert.Equal(0, PauseDecider.NextPause(3.6, cues, -1));  // 播足估計口說時長才停
    }

    [Fact]
    public void NextPause_SpokenFloor_BoundedByEstimateCapAndMaxRun()
    {
        var cues = new List<SubtitleCue>
        {
            new SubtitleCue(string.Join(' ', System.Linq.Enumerable.Repeat("w", 50)), 0.0), // 估 18s→估計上限 6s
            new SubtitleCue("next", 1.0),
        };
        Assert.Equal(-1, PauseDecider.NextPause(5.9, cues, -1)); // 估計上限 6s 未到
        Assert.Equal(0, PauseDecider.NextPause(6.0, cues, -1));  // 6s（估計上限）即停、不到 maxRun 8s
        Assert.Equal(0, PauseDecider.NextPause(3.0, cues, -1, maxRunSec: 3.0)); // maxRun 更小則由 maxRun 封頂
    }

    [Fact]
    public void EstimateSpokenSec_WordsTimesRate_CapAndEmpty()
    {
        Assert.Equal(0, PauseDecider.EstimateSpokenSec(""));
        Assert.Equal(0, PauseDecider.EstimateSpokenSec("   "));
        Assert.True(PauseDecider.EstimateSpokenSec("a b c d e") > 1.7);   // 5×0.36=1.8
        Assert.Equal(6.0, PauseDecider.EstimateSpokenSec(string.Join(' ', System.Linq.Enumerable.Repeat("w", 100)))); // 封頂 6
    }

    [Fact]
    public void CueAt_LastStartAtOrBeforeTime_NoBlankBetween()
    {
        Assert.Equal(0, PauseDecider.CueAt(2.0, Cues));
        Assert.Equal(1, PauseDecider.CueAt(3.0, Cues));   // start 含
        Assert.Equal(1, PauseDecider.CueAt(15.0, Cues));  // two 顯示至 three 開始(20)、無空窗
        Assert.Equal(2, PauseDecider.CueAt(20.0, Cues));
        Assert.Equal(2, PauseDecider.CueAt(99.0, Cues));  // 末句持續顯示至影片結束
        Assert.Equal(-1, PauseDecider.CueAt(0.5, Cues));  // 首句之前
    }

    // ── 指定說話人才暫停（增量7）──

    private static readonly List<SubtitleCue> Spoken = new()
    {
        new SubtitleCue("a", 1.0, "Ryder"),
        new SubtitleCue("b", 3.0, "Zuma"),
        new SubtitleCue("c", 5.0, "Ryder"),
        new SubtitleCue("d", 20.0, "Zuma"),
    };

    [Fact]
    public void NextPause_PauseSpeaker_OnlyPausesAtThatSpeaker_SkipsOthers()
    {
        // Ryder：a(0) 到 b 之開始(3)→暫停 0；接著跳過 b(Zuma)、暫停於 c(2)（c 暫停點＝min(d 20, c 5+8=13)=13）
        Assert.Equal(0, PauseDecider.NextPause(3.0, Spoken, -1, pauseSpeaker: "Ryder"));
        Assert.Equal(-1, PauseDecider.NextPause(3.0, Spoken, 0, pauseSpeaker: "Ryder")); // b 不停、c 暫停點未到
        Assert.Equal(2, PauseDecider.NextPause(13.0, Spoken, 0, pauseSpeaker: "Ryder")); // 跳過 b、暫停 c
    }

    [Fact]
    public void NextPause_PauseSpeaker_Null_PausesAtEveryCue()
    {
        Assert.Equal(0, PauseDecider.NextPause(3.0, Spoken, -1, pauseSpeaker: null)); // a→b 開始
        Assert.Equal(1, PauseDecider.NextPause(5.0, Spoken, 0, pauseSpeaker: null));  // b→c 開始(5)
    }

    [Fact]
    public void NextPause_PauseSpeaker_NoFurtherMatch_ReturnsMinusOne()
    {
        // 已暫停 c(2)，其後無 Ryder（d 為 Zuma）→ 不再暫停
        Assert.Equal(-1, PauseDecider.NextPause(999.0, Spoken, 2, pauseSpeaker: "Ryder"));
    }

    [Fact]
    public void SpeakerMatches_NullOrEmptyTarget_MatchesAny()
    {
        Assert.True(PauseDecider.SpeakerMatches(null, "x"));
        Assert.True(PauseDecider.SpeakerMatches("", "x"));
        Assert.True(PauseDecider.SpeakerMatches("Ryder", "Ryder"));
        Assert.False(PauseDecider.SpeakerMatches("Ryder", "Zuma"));
        Assert.False(PauseDecider.SpeakerMatches("Ryder", null));
    }
}
