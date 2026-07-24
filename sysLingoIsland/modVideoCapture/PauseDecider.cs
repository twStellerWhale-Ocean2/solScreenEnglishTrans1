using System.Text.RegularExpressions;

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

    /// <summary>倒退重置閾值（秒，#214）：播放時間相對上輪 poll 倒退達此值＝重播（ended 後）或使用者往回拉，暫停判定應自當前位置重算。正常播放輪詢單調前進、微小抖動不觸發。</summary>
    public const double RewindResetSec = 1.0;

    /// <summary>
    /// 播放時間是否大幅倒退（#214：ended 後重播／使用者往回拉）——是則呼叫端應以 <see cref="CueAt"/> 重算暫停游標，恢復逐句暫停。
    /// 任一秒數無效（&lt;0，含 <paramref name="lastPollSec"/> 初值 -1）不判倒退。純函式、可單元測試。
    /// </summary>
    public static bool IsRewind(double currentSec, double lastPollSec, double threshold = RewindResetSec) =>
        currentSec >= 0 && lastPollSec >= 0 && lastPollSec - currentSec >= threshold;

    /// <summary>
    /// 依當前播放秒數判斷是否應暫停：回「下一句（尚未暫停過）」之 index，否則 -1。
    /// start-only（#158）：一句之暫停點＝<c>min(下一句開始, 本句開始＋<paramref name="maxRunSec"/>)</c>——一般到下一句才停；
    /// 遇超長間隔則於「本句開始＋上限」先停（本句仍顯示、不乾等）。最後一句於「開始＋上限」後可停一次。
    /// <paramref name="pauseSpeaker"/>（指定說話人才暫停，增量7）：非 null／空時，只在該說話人之句暫停、其餘句略過續播不暫停（不漏該說話人之句）。
    /// <paramref name="lastPausedIndex"/>＝上次已暫停之 cue index（-1＝尚未）；cues 須依 StartSec 遞增。
    /// </summary>
    public static int NextPause(double currentSec, IReadOnlyList<SubtitleCue> cues, int lastPausedIndex,
        double maxRunSec = DefaultMaxRunSec, string? pauseSpeaker = null, bool pauseNoSpeaker = false,
        IReadOnlyCollection<string>? pauseSpeakers = null)
    {
        // 暫停對象（#189-checklist）：優先 pauseSpeakers 組（勾選面板多選）；否則退回單一 pauseSpeaker（相容既有呼叫）。
        // targets==null＝不指定（全部句皆停）；targets 為空集合＝指定但無人（無句符合→不停）。
        var targets = pauseSpeakers is { Count: > 0 } ? pauseSpeakers
                    : !string.IsNullOrEmpty(pauseSpeaker) ? new[] { pauseSpeaker }
                    : pauseSpeakers; // 空集合原樣傳遞（= 指定但無人）；純 null 才是「全部」
        var next = Math.Max(0, lastPausedIndex + 1);
        var targeted = targets is not null || pauseNoSpeaker; // 指定名單（含空）／未標示者＝針對特定對象暫停
        // 指定對象：跳過不符之句（不暫停、續播），找下一個符合者。
        // #184：未定時句（StartSec null）不列入時間判定——無已知時間可暫停於此，一律跳過（不作為 pause 目標、不當 0 秒）。
        while (next < cues.Count
               && (cues[next].StartSec is null || !PauseMatchesSet(targets, pauseNoSpeaker, cues[next].Speaker))) next++;
        if (next >= cues.Count) return -1;
        var start = cues[next].StartSec!.Value; // 已定時（null 已於上迴圈略過）
        double pausePoint;
        if (targeted)
        {
            // 指定對象（#pause-frame 修）：停在**該句起點**——畫面正落在目標句本身（選 Ryder 就停在 Ryder 說話的畫面）。
            // 停在句末＝下一句起點，畫面會落到下一位說話人（Rocky…）→ 使用者誤以為「停在 Rocky／沒停在 Ryder」。
            // 停在起點另對輪詢延遲穩健：緩衝＝整句時長，不像句末是刀口、稍慢就越過到下一句。
            pausePoint = start;
        }
        else
        {
            // 全部句（預設逐句學習）：到句末暫停（一句最多走 maxRunSec）。
            var capped = start + maxRunSec;
            // 下一句起點作為自然句末；#184：下一句未定時（null）則無已知句末→退回上限 capped（不把未定時句當時間比較對象）。
            var nextStart = next + 1 < cues.Count ? cues[next + 1].StartSec : null;
            var naturalEnd = nextStart.HasValue ? Math.Min(nextStart.Value, capped) : capped;
            // 口說時長地板（#字幕雙擊早停修正）：至少播足本句台詞估計時長——重疊/過早的下一句起點不把整句在說完前切掉
            // （雙擊跳段最有感）。仍以 maxRun 封頂、且絕不早於 naturalEnd（不縮短既有行為、不早停）。
            var spokenFloor = Math.Min(capped, start + EstimateSpokenSec(cues[next].Text));
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

    /// <summary>合唸說話人之連接詞（「Ryder <b>and</b> Marshall」「Chase <b>&amp;</b> Rubble」「A<b>,</b> B」「A<b>/</b>B」）——拆為個別名字用。</summary>
    private static readonly Regex Conjunction = new(@"\s*(?:\band\b|&|,|/|\+)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 把合唸說話人以連接詞拆為個別名字（「Ryder and Marshall」→ Ryder、Marshall；「Chase &amp; Rubble」→ Chase、Rubble）。
    /// 多詞單名（「Cap'n Turbot」「Mama eagle」「Blue-footed booby bird」）不含連接詞→原樣單一回傳、不誤拆。空/null 回空序列。
    /// 供勾選面板建「原子說話人」清單與 <see cref="SpeakerMatches"/> 共用（單一來源）。
    /// </summary>
    public static IEnumerable<string> SplitSpeakers(string? speaker)
    {
        if (string.IsNullOrEmpty(speaker)) yield break;
        foreach (var part in Conjunction.Split(speaker))
        {
            var t = part.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    /// <summary>
    /// 指定說話人是否符合（<paramref name="target"/> null／空＝任何說話人皆符合）。internal 供單元測試。
    /// #189-pause（USR：選 Ryder 也要停合唸句）：整串相符,或 <paramref name="speaker"/> 以連接詞拆出的**任一名字**＝目標——
    /// 選「Ryder」亦停在「Ryder and Marshall」「Ryder and Zuma」;多詞單名不誤拆、只整串相符。大小寫不敏感。
    /// </summary>
    internal static bool SpeakerMatches(string? target, string? speaker)
    {
        if (string.IsNullOrEmpty(target)) return true;                                     // 未指定＝全部符合
        if (string.IsNullOrEmpty(speaker)) return false;
        if (string.Equals(target, speaker, StringComparison.OrdinalIgnoreCase)) return true; // 整串相符（含直接選合唸句本身）
        foreach (var part in SplitSpeakers(speaker))                                        // 合唸句之某一名字＝目標（Ryder ∈「Ryder and Marshall」）
            if (string.Equals(target, part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 暫停對象是否符合（#189）：<paramref name="noSpeaker"/>＝true 時只在**未標示說話人**（空/null）之句暫停；
    /// 否則沿用 <see cref="SpeakerMatches"/>（指定說話人／全部）。internal 供單元測試。
    /// </summary>
    internal static bool PauseMatches(string? target, bool noSpeaker, string? speaker) =>
        noSpeaker ? string.IsNullOrEmpty(speaker) : SpeakerMatches(target, speaker);

    /// <summary>
    /// 暫停對象是否符合（一組多選，#189-checklist）：未標示句→只在 <paramref name="noSpeaker"/> 時停；具名句→
    /// <paramref name="targets"/> 為 null＝不指定（全部停）、空集合＝指定但無人（不停）、非空＝該組任一名字符合（沿用 <see cref="SpeakerMatches"/> 拆合唸句）。
    /// internal 供單元測試。
    /// </summary>
    internal static bool PauseMatchesSet(IReadOnlyCollection<string>? targets, bool noSpeaker, string? speaker)
    {
        if (string.IsNullOrEmpty(speaker)) return noSpeaker || targets is null; // 未標示句：勾了「(no speaker)」→停；或不指定名單（全部）亦停
        if (targets is null) return !noSpeaker;                                  // 具名句、不指定名單：noSpeaker-only→不停；否則（全部）停
        foreach (var t in targets) if (SpeakerMatches(t, speaker)) return true;  // 具名句、指定名單（空集合→無人符合→不停）
        return false;
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
            // #184：未定時句（StartSec null）不作為時間比較對象——不選為當前句、亦不中斷掃描（跳過續看後續已定時句）。
            var start = cues[i].StartSec;
            if (start is null) continue;
            if (start.Value <= currentSec) idx = i; else break;
        }
        return idx;
    }
}
