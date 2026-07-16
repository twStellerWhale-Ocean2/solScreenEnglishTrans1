namespace LingoIsland.Video;

/// <summary>
/// 字幕重分句（[modVideoCapture模組]，#189 Row2「AI 分析」）：把破碎/斷句錯的字幕交 AI 重新併為完整句＋標說話人，**時間不變**。
/// 供 DI 注入與測試以假實作替換；正式實作見 <see cref="OpenAiRefiner"/>。無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="RefineException"/>。
/// </summary>
public interface ISubtitleRefiner
{
    Task<RefineResult> RefineAsync(
        IReadOnlyList<SubtitleCue> cues, string? videoTitle, IProgress<string>? progress = null,
        CancellationToken ct = default, string? videoTheme = null);
}

/// <summary>重分句結果：段序列（交 <see cref="SubtitleRefine.BuildCues"/> 建出新 cue）＋API 用量（供費用記帳）。</summary>
public sealed record RefineResult(IReadOnlyList<RefinedSegment> Segments, IReadOnlyList<SpeakerUsage> Usages);

/// <summary>重分句失敗（金鑰缺失／HTTP 非 2xx／逾時／解析失敗）；訊息即人類可讀，UI 直接顯示。</summary>
public sealed class RefineException : Exception
{
    public RefineException(string message) : base(message) { }
}
