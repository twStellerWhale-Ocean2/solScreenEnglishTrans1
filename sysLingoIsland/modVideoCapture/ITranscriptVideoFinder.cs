namespace LingoIsland.Video;

/// <summary>
/// 「由字幕檔網址配影片」（[modVideoCapture模組]，epic #178 增量2〔由逐字稿改造〕，#182）：以 Responses API＋<c>web_search</c>
/// 解析使用者所貼之**字幕檔網站／網址**——(i) 單一完整字幕檔頁 或 (ii) 列多份字幕檔連結之目錄頁——逐一驗證含說話人（時間可有可無）、
/// 去重濾失效，回其標題／YouTube 連結／字幕檔來源／**字幕檔原始 URL**；UI 再據標題以 yt-dlp 定位實際影片、濾合輯、篩可載入。
/// 抽介面供測試以假實作注入、不打真網路；正式實作見 <see cref="OpenAiTranscriptVideoFinder"/>（gpt-4.1，會花 OpenAI 額度、按鈕觸發、跑前確認費用）。
/// </summary>
public interface ITranscriptVideoFinder
{
    /// <summary>解析 <paramref name="subtitleUrl"/>（單檔／目錄頁）為最多 <paramref name="max"/> 支「含說話人之字幕檔」之影片候選（可選 <paramref name="videoTheme"/> 供配對參考）；無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="SpeakerEnrichException"/>。</summary>
    Task<TranscriptVideoFindResult> FindAsync(string subtitleUrl, int max, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null);
}

/// <summary>配片結果（#182）：<see cref="Candidates"/> 候選影片清單（各帶字幕檔原始 URL）、<see cref="Usages"/> API 用量（供費用顯示、記帳）。</summary>
public sealed record TranscriptVideoFindResult(IReadOnlyList<TranscriptVideoFind.Candidate> Candidates, IReadOnlyList<SpeakerUsage> Usages);
