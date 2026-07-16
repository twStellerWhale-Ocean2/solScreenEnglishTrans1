namespace LingoIsland.Video;

/// <summary>
/// 搜尋結果 client 端過濾（#184）：依影片長度分類篩選並限筆數。純函式、可單元測試
/// （上傳日期篩選走 YouTube 伺服器端 sp token，見 <see cref="YtDlpVideoSearcher"/>，不在此）。
/// </summary>
public static class VideoSearchFilter
{
    /// <summary>
    /// 依長度鍵過濾並取前 <paramref name="max"/> 筆：<c>short</c>＝&lt;4 分、<c>medium</c>＝4–20 分、<c>long</c>＝&gt;20 分；
    /// 其他／空鍵＝不過濾。片長未知（<c>DurationSec==null</c>）於有長度篩時排除（無法歸類），不篩時保留。
    /// </summary>
    public static IReadOnlyList<VideoSearchResult> ByLength(IReadOnlyList<VideoSearchResult> results, string? lengthKey, int max)
    {
        IEnumerable<VideoSearchResult> q = lengthKey switch
        {
            "short" => results.Where(r => r.DurationSec is > 0 and < 240),
            "medium" => results.Where(r => r.DurationSec is >= 240 and <= 1200),
            "long" => results.Where(r => r.DurationSec is > 1200),
            _ => results,
        };
        return q.Take(Math.Max(0, max)).ToList();
    }
}
