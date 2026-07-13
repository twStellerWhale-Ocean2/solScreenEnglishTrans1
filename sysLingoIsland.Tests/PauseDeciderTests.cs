using System.Collections.Generic;
using LingoIsland.Video;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// [modVideoCapture模組] 導引播放到句暫停判定（PauseDecider，spec#2）：到句暫停一次、不漏句、不重暫停、
/// 不早停；當前句查找。
/// </summary>
public class PauseDeciderTests
{
    private static readonly List<SubtitleCue> Cues = new()
    {
        new SubtitleCue("one", 1.0, 3.0),
        new SubtitleCue("two", 3.0, 6.0),
        new SubtitleCue("three", 6.0, 9.0),
    };

    [Fact]
    public void NextPause_PausesAtEndOfNextCue_NotBefore()
    {
        Assert.Equal(-1, PauseDecider.NextPause(2.0, Cues, -1)); // 仍在第 0 句、未播畢→不暫停
        Assert.Equal(-1, PauseDecider.NextPause(2.999, Cues, -1));
        Assert.Equal(0, PauseDecider.NextPause(3.0, Cues, -1));  // 第 0 句播畢→暫停於 0
    }

    [Fact]
    public void NextPause_DoesNotRepauseSameCue()
    {
        Assert.Equal(-1, PauseDecider.NextPause(3.5, Cues, 0)); // 已暫停 0、第 1 句未畢
        Assert.Equal(1, PauseDecider.NextPause(6.0, Cues, 0));  // 第 1 句播畢→暫停於 1
    }

    [Fact]
    public void NextPause_NoSkip_ReturnsImmediateNextEvenIfTimeJumps()
    {
        // 即使時間跳過第 2 句結束，下一個暫停仍是緊接的未暫停句（不跳句）
        Assert.Equal(1, PauseDecider.NextPause(9.0, Cues, 0));
    }

    [Fact]
    public void NextPause_PastLastCue_ReturnsMinusOne()
    {
        Assert.Equal(-1, PauseDecider.NextPause(20.0, Cues, 2));
    }

    [Fact]
    public void NextPause_EmptyCues_ReturnsMinusOne()
    {
        Assert.Equal(-1, PauseDecider.NextPause(5.0, new List<SubtitleCue>(), -1));
    }

    [Fact]
    public void CueAt_ReturnsContainingCue_StartInclusiveEndExclusive()
    {
        Assert.Equal(0, PauseDecider.CueAt(2.0, Cues));
        Assert.Equal(1, PauseDecider.CueAt(3.0, Cues));   // 邊界：start 含
        Assert.Equal(2, PauseDecider.CueAt(8.9, Cues));
        Assert.Equal(-1, PauseDecider.CueAt(0.5, Cues));  // 首句之前
        Assert.Equal(-1, PauseDecider.CueAt(9.0, Cues));  // 末句 end（不含）
    }
}
