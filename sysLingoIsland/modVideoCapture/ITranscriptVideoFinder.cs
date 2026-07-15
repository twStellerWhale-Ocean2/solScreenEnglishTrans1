namespace LingoIsland.Video;

/// <summary>
/// 「由逐字稿找影片」（[modVideoCapture模組]，#189 獲得頁重構「搜尋區塊·由逐字稿」子頁）：以 Responses API＋<c>web_search</c>
/// 找「該主題**有公開逐字稿可用**」之 YouTube 影片候選（先找逐字稿、再由 UI 定位影片）。
/// 抽介面供測試以假實作注入、不打真網路；正式實作見 <see cref="OpenAiTranscriptVideoFinder"/>（gpt-4.1，會花 OpenAI 額度、按鈕觸發、跑前確認費用）。
/// </summary>
public interface ITranscriptVideoFinder
{
    /// <summary>找最多 <paramref name="max"/> 支有逐字稿可用之影片候選（可選 <paramref name="videoTheme"/> 縮小範圍）；無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="SpeakerEnrichException"/>。</summary>
    Task<TranscriptVideoFindResult> FindAsync(string topic, int max, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null);
}

/// <summary>找影片結果（#189）：<see cref="Candidates"/> 候選影片清單、<see cref="Usages"/> API 用量（供費用顯示、記帳）。</summary>
public sealed record TranscriptVideoFindResult(IReadOnlyList<TranscriptVideoFind.Candidate> Candidates, IReadOnlyList<SpeakerUsage> Usages);
