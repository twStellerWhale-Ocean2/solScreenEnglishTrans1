namespace LingoIsland.Video;

/// <summary>
/// 說話人語句數統計（[modPresent模組] 說話人勾選面板顯示語句數，#196）：純函式、不依賴 UI，
/// 以注入 cue 清單可單元測試。與勾選面板同一「拆原子」口徑——合唸句（<c>Peppa/Suzy:</c>）經
/// <see cref="PauseDecider.SplitSpeakers"/> 拆為個別名字後，對每位參與者各計一次。
/// 計數僅供呈現層顯示（名字尾端括弧數字），不參與篩選／暫停／字型色／勾選保留之比對（那些一律以純名字為 key）。
/// </summary>
public static class SpeakerTally
{
    /// <summary>
    /// 統計各原子說話人之語句數：某句之說話人經 <see cref="PauseDecider.SplitSpeakers"/> 拆分後，
    /// 其中每個相異名字各計一次（同一句內同名不重複計）。空說話人之句不計入任何名字。
    /// 回傳字典以 <paramref name="comparer"/>（預設 <see cref="StringComparer.OrdinalIgnoreCase"/>）為鍵比較器，
    /// 與面板去重排序之比較器一致。
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountBySpeaker(
        IEnumerable<SubtitleCue> cues, IEqualityComparer<string>? comparer = null)
    {
        comparer ??= StringComparer.OrdinalIgnoreCase;
        var counts = new Dictionary<string, int>(comparer);
        foreach (var c in cues)
        {
            if (string.IsNullOrEmpty(c.Speaker)) continue;
            foreach (var atom in PauseDecider.SplitSpeakers(c.Speaker).Distinct(comparer))
            {
                counts[atom] = counts.TryGetValue(atom, out var n) ? n + 1 : 1;
            }
        }
        return counts;
    }

    /// <summary>
    /// 依語句數**遞減**排序原子說話人名（同數以名字 <paramref name="comparer"/> 遞增 tie-break，穩定、決定性），
    /// 供勾選面板「主要說話人置頂」呈現（#201）。<paramref name="counts"/> 缺鍵者以 0 計；純函式、可單元測試、不改比對邏輯。
    /// 呼叫端負責把 `（全部說話人）` 置首、`（無說話人）` 置尾——本函式只排具名說話人。
    /// </summary>
    public static IReadOnlyList<string> OrderByLineCountDesc(
        IEnumerable<string> atoms, IReadOnlyDictionary<string, int> counts, IComparer<string>? comparer = null)
    {
        comparer ??= StringComparer.OrdinalIgnoreCase;
        return atoms
            .OrderByDescending(a => counts.TryGetValue(a, out var n) ? n : 0)
            .ThenBy(a => a, comparer)
            .ToList();
    }

    /// <summary>「（全部說話人）」列之語句數＝本片總句數（所有 cue）。</summary>
    public static int TotalCount(IReadOnlyCollection<SubtitleCue> cues) => cues.Count;

    /// <summary>「（無說話人）」列之語句數＝未標說話人之句數。</summary>
    public static int NoSpeakerCount(IEnumerable<SubtitleCue> cues)
        => cues.Count(c => string.IsNullOrEmpty(c.Speaker));
}
