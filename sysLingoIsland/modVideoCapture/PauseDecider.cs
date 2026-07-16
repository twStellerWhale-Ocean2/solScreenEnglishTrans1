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
    /// <paramref name="pauseSpeaker"/>（指定說話人才暫停，增量7）：非 null／空時，只在該說話人之句暫停、其餘句略過續播不暫停（不漏該說話人之句）。
    /// <paramref name="lastPausedIndex"/>＝上次已暫停之 cue index（-1＝尚未）；cues 須依 StartSec 遞增。
    /// </summary>
    public static int NextPause(double currentSec, IReadOnlyList<SubtitleCue> cues, int lastPausedIndex,
        double maxRunSec = DefaultMaxRunSec, string? pauseSpeaker = null, bool pauseNoSpeaker = false)
    {
        var next = Math.Max(0, lastPausedIndex + 1);
        var targeted = !string.IsNullOrEmpty(pauseSpeaker) || pauseNoSpeaker; // 指定說話人／未標示者＝針對特定對象暫停
        // 指定對象：跳過不符之句（不暫停、續播），找下一個符合者
        while (next < cues.Count && !PauseMatches(pauseSpeaker, pauseNoSpeaker, cues[next].Speaker)) next++;
        if (next >= cues.Count) return -1;
        double pausePoint;
        if (targeted)
        {
            // 指定對象（#pause-frame 修）：停在**該句起點**——畫面正落在目標句本身（選 Ryder 就停在 Ryder 說話的畫面）。
            // 停在句末＝下一句起點，畫面會落到下一位說話人（Rocky…）→ 使用者誤以為「停在 Rocky／沒停在 Ryder」。
            // 停在起點另對輪詢延遲穩健：緩衝＝整句時長，不像句末是刀口、稍慢就越過到下一句。
            pausePoint = cues[next].StartSec;
        }
        else
        {
            // 全部句（預設逐句學習）：到句末暫停（一句最多走 maxRunSec）。
            var capped = cues[next].StartSec + maxRunSec;
            var naturalEnd = next + 1 < cues.Count ? Math.Min(cues[next + 1].StartSec, capped) : capped;
            // 口說時長地板（#字幕雙擊早停修正）：至少播足本句台詞估計時長——重疊/過早的下一句起點不把整句在說完前切掉
            // （雙擊跳段最有感）。仍以 maxRun 封頂、且絕不早於 naturalEnd（不縮短既有行為、不早停）。
            var spokenFloor = Math.Min(capped, cues[next].StartSec + EstimateSpokenSec(cues[next].Text));
            pausePoint = Math.Max(naturalEnd, spokenFloor);
        }
        return currentSec >= pausePoint ? next : -1;
    }

    /// <summary>估計一句台詞之口說秒數（#字幕雙擊早停修正）：詞數×每詞約 0.36 秒（≈165 wpm），上限 6 秒。供暫停地板，避免重疊字幕把整句在說完前切掉；空句回 0（不影響）。internal 供單元測試。</summary>
    internal static double EstimateSpokenSec(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Min(words * 0.36, 6.0);
    }

    /// <summary>指定說話人是否符合（<paramref name="target"/> null／空＝任何說話人皆符合）。internal 供單元測試。</summary>
    internal static bool SpeakerMatches(string? target, string? speaker) =>
        string.IsNullOrEmpty(target) || string.Equals(target, speaker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 暫停對象是否符合（#189）：<paramref name="noSpeaker"/>＝true 時只在**未標示說話人**（空/null）之句暫停；
    /// 否則沿用 <see cref="SpeakerMatches"/>（指定說話人／全部）。internal 供單元測試。
    /// </summary>
    internal static bool PauseMatches(string? target, bool noSpeaker, string? speaker) =>
        noSpeaker ? string.IsNullOrEmpty(speaker) : SpeakerMatches(target, speaker);

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
