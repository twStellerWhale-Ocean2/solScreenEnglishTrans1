namespace LingoIsland.Video;

/// <summary>
/// 導引播放到句暫停判定（[techItem影片播放]／[modVideoCapture模組] 影片擷取契約，spec#2）：純函式、
/// 不依賴 UI／瀏覽器、以假時間／cue 注入可單元測試。到每句結束即暫停一次（不漏句、不重複暫停同一句、不早停）。
/// </summary>
public static class PauseDecider
{
    /// <summary>
    /// 依當前播放秒數判斷是否應暫停：回「下一句（尚未暫停過且已播畢）」之 index，否則 -1。
    /// <paramref name="lastPausedIndex"/>＝上次已暫停之 cue index（-1＝尚未）；cues 須依 StartSec 遞增。
    /// 逐句推進（即使時間跳過多句，仍回緊接的下一句、不跳句）。
    /// </summary>
    public static int NextPause(double currentSec, IReadOnlyList<SubtitleCue> cues, int lastPausedIndex)
    {
        var next = lastPausedIndex + 1;
        if (next >= 0 && next < cues.Count && currentSec >= cues[next].EndSec) return next;
        return -1;
    }

    /// <summary>回含 <paramref name="currentSec"/> 之 cue index（顯示當前句字幕用；start 含、end 不含），無則 -1。</summary>
    public static int CueAt(double currentSec, IReadOnlyList<SubtitleCue> cues)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            if (currentSec >= cues[i].StartSec && currentSec < cues[i].EndSec) return i;
        }
        return -1;
    }
}
