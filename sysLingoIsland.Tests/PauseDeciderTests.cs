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
}
