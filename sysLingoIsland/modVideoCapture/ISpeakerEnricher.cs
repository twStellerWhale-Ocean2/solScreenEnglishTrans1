namespace LingoIsland.Video;

/// <summary>
/// 說話人來源（[modVideoCapture模組]，epic #145 增量6，#156）：對既有逐句字幕**推斷／查得**每句說話人，
/// 供影片頁按鈕觸發之非破壞疊加。多來源（AI 推斷、wiki／網路台詞…）共用此介面，可插拔；
/// 回傳與 <paramref name="cues"/> 等長之逐句說話人（null＝該句未知），由 <see cref="SpeakerInference.MergeSpeakers"/> 疊加。
/// </summary>
public interface ISpeakerEnricher
{
    /// <summary>依 <paramref name="cues"/>（與可選 <paramref name="videoTitle"/>）取每句說話人＋各次 API 用量；<paramref name="progress"/> 逐步回報進度（供對話視窗顯示、減少等待焦慮）；失敗擲 <see cref="SpeakerEnrichException"/>。</summary>
    Task<SpeakerEnrichResult> InferSpeakersAsync(
        IReadOnlyList<SubtitleCue> cues, string? videoTitle, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>某次 AI 呼叫之 token 用量＋模型＋是否含 web_search（供費用估算，AI 動作對話視窗）。</summary>
public sealed record SpeakerUsage(int InputTokens, int OutputTokens, int TotalTokens, string Model = "", bool WebSearch = false);

/// <summary>說話人來源之結果：逐句說話人（null＝該句未知）＋本流程各 API 呼叫之用量（多筆；混模型時逐筆估價再加總）。</summary>
public sealed record SpeakerEnrichResult(IReadOnlyList<string?> Speakers, IReadOnlyList<SpeakerUsage> Usages);

/// <summary>說話人疊加之明確可讀失敗（無金鑰、網路錯、逾時、回應無法解析等）——中止該次疊加、不當機不無聲失敗。</summary>
public sealed class SpeakerEnrichException : Exception
{
    public SpeakerEnrichException(string message) : base(message) { }
}
