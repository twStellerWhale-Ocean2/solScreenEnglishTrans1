namespace LingoIsland.Video;

/// <summary>
/// 導引播放到句暫停判定（[techItem影片播放]／[modVideoCapture模組] 影片擷取契約，spec#2）：純函式、
/// 不依賴 UI／瀏覽器、以假時間／cue 注入可單元測試。到每句結束即暫停一次（不漏句、不重複暫停同一句、不早停）。
/// start-only（#158）：字幕只留開始時間，一句顯示至下一句開始；暫停點與顯示解耦（暫停可加上限、顯示無空窗）。
/// </summary>
public static class PauseDecider
{
    /// <summary>start-only 到句暫停之預設「單句最長走時」上限（秒，#158）：一句最多走這麼久即先暫停（本句仍顯示），避免超長靜默間隔乾等。</summary>
    public const double DefaultMaxRunSec = 8.0;

    /// <summary>
    /// 依當前播放秒數判斷是否應暫停：回「下一句（尚未暫停過）」之 index，否則 -1。
    /// start-only（#158）：一句之暫停點＝<c>min(下一句開始, 本句開始＋<paramref name="maxRunSec"/>)</c>——一般到下一句才停；
    /// 遇超長間隔則於「本句開始＋上限」先停（本句仍顯示、不乾等）。最後一句於「開始＋上限」後可停一次。
    /// <paramref name="lastPausedIndex"/>＝上次已暫停之 cue index（-1＝尚未）；cues 須依 StartSec 遞增。逐句推進、不跳句。
    /// </summary>
    public static int NextPause(double currentSec, IReadOnlyList<SubtitleCue> cues, int lastPausedIndex,
        double maxRunSec = DefaultMaxRunSec)
    {
        var next = lastPausedIndex + 1;
        if (next < 0 || next >= cues.Count) return -1;
        var capped = cues[next].StartSec + maxRunSec;
        var pausePoint = next + 1 < cues.Count ? Math.Min(cues[next + 1].StartSec, capped) : capped;
        return currentSec >= pausePoint ? next : -1;
    }

    /// <summary>
    /// 回含 <paramref name="currentSec"/> 之 cue index（顯示當前句用）：start-only 一句顯示至下一句開始（無空窗），
    /// 即回「起點 &lt;= <paramref name="currentSec"/> 之最後一句」；<paramref name="currentSec"/> 早於首句回 -1。cues 須依 StartSec 遞增。
    /// </summary>
    public static int CueAt(double currentSec, IReadOnlyList<SubtitleCue> cues)
    {
        var idx = -1;
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].StartSec <= currentSec) idx = i; else break;
        }
        return idx;
    }
}
