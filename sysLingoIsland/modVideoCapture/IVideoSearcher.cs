namespace LingoIsland.Video;

/// <summary>YouTube 搜尋結果一筆（[techItem字幕擷取] 延伸，#171／#177）：影片 ID＋標題＋片長秒數（<see cref="DurationSec"/>，直播/未知為 null）。縮圖由 UI 依 ID 組 YouTube 縮圖 URL 載入。</summary>
public sealed record VideoSearchResult(string VideoId, string Title, int? DurationSec = null);

/// <summary>
/// 依關鍵字搜尋 YouTube 影片（#171）：供影片頁「依主題搜尋關鍵字查 YouTube、選單點選載入」。
/// 抽介面供測試以假實作注入、不打真網路／不起 CLI；正式實作見 <see cref="YtDlpVideoSearcher"/>（本機 yt-dlp、免額外金鑰）。
/// </summary>
public interface IVideoSearcher
{
    /// <summary>回關鍵字之前 <paramref name="max"/> 筆 YouTube 結果；<paramref name="uploadDateToken"/> 非空時以 YouTube 上傳日期篩選（sp token，如本月/今年）；空關鍵字回空清單；失敗擲 <see cref="SubtitleException"/>。</summary>
    Task<IReadOnlyList<VideoSearchResult>> SearchAsync(string query, int max = 8, CancellationToken ct = default, string? uploadDateToken = null);
}
