using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 匯入字幕之自動屏蔽（#217，[modVideoCapture模組]）：把主題設定之屏蔽字串（如 `(SNORT)` 音效標記）自各句台詞移除。
/// 純函式、可單元測試；套用於**匯入時**（單檔載入與批次加入），已入庫字幕不回溯（清快取重載即重套）。
/// </summary>
public static class SubtitleBlocklist
{
    /// <summary>
    /// 逐句移除屏蔽字串（**大小寫不敏感之純字串比對**、非 regex——`(SNORT)` 等括號字面不誤解），移除後收合多餘空白；
    /// 台詞移空之句**整句剔除**（原句僅音效標記）。說話人與時間不動；無屏蔽字串＝原清單原樣返回。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Remove(IReadOnlyList<SubtitleCue> cues, IReadOnlyCollection<string>? blockedWords)
    {
        if (cues.Count == 0 || blockedWords is null || blockedWords.Count == 0) { return cues; }
        var result = new List<SubtitleCue>(cues.Count);
        foreach (var cue in cues)
        {
            var text = cue.Text ?? "";
            foreach (var w in blockedWords)
            {
                if (!string.IsNullOrEmpty(w)) { text = text.Replace(w, " ", StringComparison.OrdinalIgnoreCase); }
            }
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length == 0) { continue; }                                  // 整句只剩屏蔽字串→剔除
            result.Add(text == cue.Text ? cue : cue with { Text = text });
        }
        return result;
    }
}
