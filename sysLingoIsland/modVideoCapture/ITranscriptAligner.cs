namespace LingoIsland.Video;

/// <summary>
/// 字幕主線之 AI 抽取（[modVideoCapture模組]，epic #178 增量6′-B「時間 pivot」定案）：字幕唯一來源＝
/// **字幕檔本身**——其自帶之時間＋說話人直接採用（不聲音對齊、不跑 Whisper、不抓 YouTube 字幕）。
/// 僅當版面五花八門、免費解析（<see cref="SubtitleParser"/>）讀不到時間時，才以 <see cref="ExtractTimedCuesAsync"/>
/// 讓 AI 讀網頁純文字**逐句照抄原有時間戳**（非估算/對齊，故不亂序）。
/// 抽介面供測試以假實作注入、不打真網路；正式實作見 <see cref="OpenAiTranscriptAligner"/>（會花 OpenAI 額度、由使用者觸發、跑前確認費用）。
/// <b>說話人取自字幕檔本身（非推斷、非 YouTube 字幕）</b>——此即本 app 差異化。
/// </summary>
public interface ITranscriptAligner
{
    /// <summary>
    /// 直接抽取（epic #178 增量6′-B「時間 pivot」定案）：讀字幕/逐字稿**網頁純文字**（版面五花八門），**逐句抽出「時間戳＋說話人＋台詞」**——
    /// 時間戳一律**照網頁原樣抄出**（AI 讀結構化資料、**不推算/不對齊/不編造時間**，故不亂序）。供標準格式（VTT/SRT/固定式逐字稿）之免費解析器讀不到時的 fallback。
    /// 無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="SpeakerEnrichException"/>；使用者取消傳遞 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<SubtitleExtractResult> ExtractTimedCuesAsync(
        string rawTranscript, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>AI 直接抽取結果（增量6′-B）：逐句 <see cref="SubtitleCue"/>（時間＋說話人＋台詞,時間照網頁原樣、非估算）＋API 用量＋<see cref="Truncated"/>（輸出被上限截斷＝頁面過長、結果不可靠,呼叫端據此給明確錯誤）。</summary>
public sealed record SubtitleExtractResult(IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<SpeakerUsage> Usages, bool Truncated = false);
