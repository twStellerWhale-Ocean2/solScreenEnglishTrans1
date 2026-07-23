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
    public void NextPause_PauseSpeaker_PausesAtThatSpeakersLineStart_SkipsOthers()
    {
        // #pause-frame 修：指定說話人停在**該句起點**（畫面落在該說話人本身，不會到下一位）。
        // Ryder：a 起點(1) 停 0；接著跳過 b(Zuma)、於 c 起點(5) 停 2。
        Assert.Equal(-1, PauseDecider.NextPause(0.9, Spoken, -1, pauseSpeaker: "Ryder")); // a 起點未到
        Assert.Equal(0, PauseDecider.NextPause(1.0, Spoken, -1, pauseSpeaker: "Ryder"));  // 停在 a 起點
        Assert.Equal(-1, PauseDecider.NextPause(4.9, Spoken, 0, pauseSpeaker: "Ryder")); // b 不停、c 起點未到
        Assert.Equal(2, PauseDecider.NextPause(5.0, Spoken, 0, pauseSpeaker: "Ryder"));  // 跳過 b(Zuma)、停在 c 起點
    }

    [Fact]
    public void NextPause_PauseSpeaker_PauseFrameIsTargetLine_NotNextSpeaker()
    {
        // 修前 bug：指定說話人停在句末＝下一句起點→畫面落在下一位說話人。
        // 修後停在起點→暫停秒之 CueAt 正是目標句本身（畫面＝該說話人）。
        var pausePoint = Spoken[2].StartSec!.Value;                                 // c(Ryder) 起點=5（#184：StartSec 改 double?）
        Assert.Equal(2, PauseDecider.NextPause(pausePoint, Spoken, 0, pauseSpeaker: "Ryder")); // 停在 c
        Assert.Equal(2, PauseDecider.CueAt(pausePoint, Spoken));                     // 畫面正是 c(Ryder)、非 d(Zuma)
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

    [Fact]
    public void SpeakerMatches_JointSpeaker_MatchesEachName()
    {
        // USR：選「Ryder」也要停在含 Ryder 的合唸句（「Ryder and Marshall」「Ryder and Zuma」）。
        Assert.True(PauseDecider.SpeakerMatches("Ryder", "Ryder and Marshall"));
        Assert.True(PauseDecider.SpeakerMatches("Ryder", "Ryder and Zuma"));
        Assert.True(PauseDecider.SpeakerMatches("Marshall", "Ryder and Marshall")); // 另一名字亦符合
        Assert.True(PauseDecider.SpeakerMatches("Rubble", "Chase & Rubble"));        // & 連接
        Assert.True(PauseDecider.SpeakerMatches("Zuma", "Ryder, Zuma"));             // 逗號連接
        Assert.True(PauseDecider.SpeakerMatches("Ryder and Marshall", "Ryder and Marshall")); // 直接選合唸句本身
    }

    [Fact]
    public void SpeakerMatches_MultiWordSingleName_NotSplit()
    {
        // 多詞單名不含連接詞→不誤拆:選「Cap'n」不應停在「Cap'n Turbot」（只整串相符）。
        Assert.True(PauseDecider.SpeakerMatches("Cap'n Turbot", "Cap'n Turbot"));
        Assert.False(PauseDecider.SpeakerMatches("Cap'n", "Cap'n Turbot"));
        Assert.False(PauseDecider.SpeakerMatches("booby", "Blue-footed booby bird"));
        Assert.False(PauseDecider.SpeakerMatches("Ryder", "Ryder's dad"));           // 非連接詞、不誤中
        Assert.False(PauseDecider.SpeakerMatches("and", "Ryder and Marshall"));       // 連接詞本身不是名字
    }

    [Fact]
    public void SplitSpeakers_JointToAtoms_SingleNameUntouched()
    {
        Assert.Equal(new[] { "Ryder", "Marshall" }, PauseDecider.SplitSpeakers("Ryder and Marshall"));
        Assert.Equal(new[] { "Chase", "Rubble" }, PauseDecider.SplitSpeakers("Chase & Rubble"));
        Assert.Equal(new[] { "Ryder", "Zuma" }, PauseDecider.SplitSpeakers("Ryder, Zuma"));
        Assert.Equal(new[] { "Cap'n Turbot" }, PauseDecider.SplitSpeakers("Cap'n Turbot")); // 多詞單名不誤拆
        Assert.Empty(PauseDecider.SplitSpeakers(""));
        Assert.Empty(PauseDecider.SplitSpeakers(null));
    }

    [Fact]
    public void PauseMatchesSet_NullEveryone_EmptyNone_MembersJoint()
    {
        Assert.True(PauseDecider.PauseMatchesSet(null, false, "Ryder"));    // 不指定名單→全部停
        Assert.False(PauseDecider.PauseMatchesSet(null, true, "Ryder"));    // noSpeaker-only→具名句不停
        Assert.True(PauseDecider.PauseMatchesSet(null, true, ""));          // noSpeaker→未標示句停
        var set = new[] { "Ryder", "Zuma" };
        Assert.True(PauseDecider.PauseMatchesSet(set, false, "Ryder"));
        Assert.True(PauseDecider.PauseMatchesSet(set, false, "Ryder and Marshall")); // 合唸句含 Ryder
        Assert.False(PauseDecider.PauseMatchesSet(set, false, "Rocky"));
        Assert.False(PauseDecider.PauseMatchesSet(System.Array.Empty<string>(), false, "Ryder")); // 空集合→無人符合
        Assert.False(PauseDecider.PauseMatchesSet(set, false, ""));         // 未標示句、未勾 noSpeaker→不停
    }

    [Fact]
    public void NextPause_PauseSpeakersSet_StopsAtAnyMemberStart_SkipsOthers()
    {
        var cues = new List<SubtitleCue>
        {
            new("a", 1.0, "Ryder"),
            new("b", 3.0, "Rocky"),
            new("c", 5.0, "Ryder and Zuma"),  // 合唸句含 Zuma
            new("d", 7.0, "Marshall"),
        };
        var set = new[] { "Ryder", "Zuma" };
        Assert.Equal(0, PauseDecider.NextPause(1.0, cues, -1, pauseSpeakers: set));   // 停 Ryder 起點
        Assert.Equal(2, PauseDecider.NextPause(5.0, cues, 0, pauseSpeakers: set));    // 跳過 Rocky、停「Ryder and Zuma」
        Assert.Equal(-1, PauseDecider.NextPause(999.0, cues, 2, pauseSpeakers: set)); // 其後僅 Marshall（不在名單）→不停
        Assert.Equal(-1, PauseDecider.NextPause(999.0, cues, -1, pauseSpeakers: System.Array.Empty<string>())); // 空集合→全不停
    }

    // ── 只在未標示（unknown）之句暫停（#189）──
    private static readonly List<SubtitleCue> Mixed = new()
    {
        new SubtitleCue("a", 1.0, "Ryder"), // 具名
        new SubtitleCue("b", 3.0),           // 未標示
        new SubtitleCue("c", 5.0, "Zuma"),  // 具名
        new SubtitleCue("d", 20.0),          // 未標示
    };

    [Fact]
    public void NextPause_PauseNoSpeaker_OnlyPausesAtUnlabeled_SkipsLabeled()
    {
        // #pause-frame 修：未標示者亦停在**該句起點**（畫面落在該未標示句本身）。
        Assert.Equal(-1, PauseDecider.NextPause(2.9, Mixed, -1, pauseNoSpeaker: true));
        Assert.Equal(1, PauseDecider.NextPause(3.0, Mixed, -1, pauseNoSpeaker: true));   // 跳過 a(Ryder)、於 b 起點(3) 暫停
        Assert.Equal(-1, PauseDecider.NextPause(19.9, Mixed, 1, pauseNoSpeaker: true));  // 跳過 c(Zuma)、d 起點未到
        Assert.Equal(3, PauseDecider.NextPause(20.0, Mixed, 1, pauseNoSpeaker: true));   // 於末句 d 起點(20) 暫停
        Assert.Equal(-1, PauseDecider.NextPause(999.0, Mixed, 3, pauseNoSpeaker: true)); // d 後無未標示句
    }

    [Fact]
    public void PauseMatches_NoSpeaker_MatchesOnlyEmpty()
    {
        Assert.True(PauseDecider.PauseMatches(null, noSpeaker: true, null));    // 未標示→符合
        Assert.True(PauseDecider.PauseMatches(null, noSpeaker: true, ""));
        Assert.False(PauseDecider.PauseMatches(null, noSpeaker: true, "Ryder")); // 有名→不符合
        Assert.True(PauseDecider.PauseMatches("Ryder", noSpeaker: false, "Ryder")); // noSpeaker=false 沿用 SpeakerMatches
        Assert.False(PauseDecider.PauseMatches("Ryder", noSpeaker: false, "Zuma"));
        Assert.True(PauseDecider.PauseMatches(null, noSpeaker: false, "anyone"));
    }

    // ── 時間未知（StartSec null，#184 增量4）：未定時句不列入時間判定，不崩不誤判 ──

    // a(1) 定時、mid(null) 未定時、c(5) 定時：未定時句居中，驗容忍（現況產生器排最後，此為防禦性測試）
    private static readonly List<SubtitleCue> WithUntimed = new()
    {
        new SubtitleCue("a", 1.0),
        new SubtitleCue("mid", (double?)null),  // 時間未知
        new SubtitleCue("c", 5.0),
    };

    [Fact]
    public void NextPause_UntimedCue_NeverAPauseTarget_TimedStillPause()
    {
        // 已暫停 a(index 0)後：next=1 為未定時→跳過、落在 c(index 2)，暫停點＝c 起點+上限(5+8=13)。
        // 未定時 mid(index 1) 絕不被回為暫停目標，且未被當 0 秒（否則會於 currentSec>=0 立即回 1）。
        Assert.Equal(-1, PauseDecider.NextPause(0.0, WithUntimed, 0));   // 不於 0 秒對未定時句連環暫停
        Assert.Equal(-1, PauseDecider.NextPause(12.9, WithUntimed, 0));  // c 暫停點(13)未到、mid 不算
        Assert.Equal(2, PauseDecider.NextPause(13.0, WithUntimed, 0));   // 跳過 mid、於 c 起點+上限暫停
    }

    [Fact]
    public void NextPause_TimedBeforeUntimed_CapsAtMaxRun_NotUntimedStart()
    {
        // a(1) 之後緊接未定時句→無已知句末，退回「起點+上限」(1+8=9) 暫停（不把未定時句當時間比較對象）。
        Assert.Equal(-1, PauseDecider.NextPause(8.9, WithUntimed, -1));
        Assert.Equal(0, PauseDecider.NextPause(9.0, WithUntimed, -1)); // a 於 1+8=9 暫停
    }

    [Fact]
    public void NextPause_AllUntimed_ReturnsMinusOne()
    {
        var allNull = new List<SubtitleCue> { new("x", (double?)null), new("y", (double?)null) };
        Assert.Equal(-1, PauseDecider.NextPause(0.0, allNull, -1));
        Assert.Equal(-1, PauseDecider.NextPause(999.0, allNull, -1));
    }

    [Fact]
    public void CueAt_UntimedCue_NotSelected_ScanContinuesToLaterTimed()
    {
        // 未定時 mid 不作為時間比較對象：不被選為當前句（currentSec=3 仍回 a，而非 mid），且不中斷掃描（能續看到 c）。
        Assert.Equal(0, PauseDecider.CueAt(3.0, WithUntimed));  // mid 未被當 0 秒選中→仍是 a
        Assert.Equal(0, PauseDecider.CueAt(4.9, WithUntimed));
        Assert.Equal(2, PauseDecider.CueAt(6.0, WithUntimed));  // 掃描跨過 mid、選到 c（未中斷）
    }

    [Fact]
    public void CueAt_AllUntimed_ReturnsMinusOne()
    {
        var allNull = new List<SubtitleCue> { new("x", (double?)null), new("y", (double?)null) };
        Assert.Equal(-1, PauseDecider.CueAt(50.0, allNull));
    }

    // ── 導航一次性暫停 override 之語意（#208，修「上一句彈回」）──
    // UI 導航（上一句/下一句/雙擊跳句）至句 i 後，以 NextPause(t, cues, i-1)（無 targets＝全部句規則）判
    // 「句 i 句末必停」；若沿用說話人勾選規則，導航至非勾選句會跳過它、停在後面勾選句起點＝彈回原句（病因對照）。
    private static readonly List<SubtitleCue> NavCues = new()
    {
        new SubtitleCue("hi", 1.0, "Peppa"),
        new SubtitleCue("oink", 3.0, "George"),  // 導航目標：非勾選句
        new SubtitleCue("dear", 5.0, "Peppa"),   // 勾選句（targeted 停其起點）
    };

    [Fact]
    public void NextPause_NavOverride_PausesNavTargetAtItsEnd_IgnoresSpeakerFilter()
    {
        // 導航至句 1（George、非勾選）：override 呼叫形＝lastPausedIndex=0、無 targets → 句 1 於下一句開始（5.0）暫停。
        Assert.Equal(-1, PauseDecider.NextPause(3.2, NavCues, 0));  // 句中不停（不早停）
        Assert.Equal(-1, PauseDecider.NextPause(4.9, NavCues, 0));
        Assert.Equal(1, PauseDecider.NextPause(5.0, NavCues, 0));   // 句 1 句末＝必停（override 語意）
    }

    [Fact]
    public void NextPause_SpeakerFilterOnly_WouldBounceBackToTargetedCue()
    {
        // 病因對照（#208）：同一情境若仍帶說話人勾選（只停 Peppa），句 1（George）被跳過、
        // 於句 2（Peppa）起點 5.0 即停——畫面落回勾選句＝使用者所見「按上一句又彈回」。
        Assert.Equal(-1, PauseDecider.NextPause(4.9, NavCues, 0, pauseSpeakers: new[] { "Peppa" }));
        Assert.Equal(2, PauseDecider.NextPause(5.0, NavCues, 0, pauseSpeakers: new[] { "Peppa" })); // 停句 2 起點、非句 1 句末
    }
}
