namespace LingoIsland.Video;

/// <summary>
/// 字幕主線之兩段 AI 作業（[modVideoCapture模組]，epic #178 增量5′〔字幕主線 pivot〕）：字幕唯一來源＝
/// **字幕檔（含說話人）＋Whisper 聲音對齊**——(1) <see cref="ParseTranscriptAsync"/> 把字幕檔原文整理成
/// **逐句（說話人＋台詞）序列**（敘事序、無時間）；(2) <see cref="AlignAsync"/> 把這些句對齊到 Whisper 逐句時間軸
/// （聲音真實時間），回**每句對應之開始秒**（對不上者留 <c>null</c>＝時間未知）。組合後即帶說話人＋時間之字幕。
/// 抽介面供測試以假實作注入、不打真網路；正式實作見 <see cref="OpenAiTranscriptAligner"/>（會花 OpenAI 額度、由使用者觸發、跑前確認費用）。
/// <b>說話人取自字幕檔本身（非推斷、非 YouTube 字幕）、時間取自 Whisper（真實發音）</b>——此即本 app 差異化。
/// </summary>
public interface ITranscriptAligner
{
    /// <summary>
    /// 把字幕檔原文（可為網頁純文字或字幕檔內容，已去 HTML）整理成**逐句（說話人＋台詞）序列**（敘事序、無時間）：
    /// 略過導覽／廣告／標題／純舞台指示，說話人取自「角色：台詞」之角色名（無則空）。以 AI 解析、不上網。
    /// 無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="SpeakerEnrichException"/>；使用者取消傳遞 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<TranscriptParseResult> ParseTranscriptAsync(
        string rawTranscript, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// 把 <paramref name="lines"/>（字幕檔逐句、含說話人、無時間）逐塊對齊到 <paramref name="audioCues"/>
    /// （Whisper 逐句、含時間、無說話人），回**每句對應之開始秒**（依 <paramref name="lines"/> 順序、對不上者 <c>null</c>）。
    /// 逐塊送模型「對照聲音時間軸標時間」（不上網、便宜模型）；時間應隨敘事單調不遞減。
    /// 無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="SpeakerEnrichException"/>；使用者取消傳遞 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<TranscriptAlignResult> AlignAsync(
        IReadOnlyList<TranscriptLine> lines, IReadOnlyList<SubtitleCue> audioCues,
        IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>字幕檔整理後之一句：說話人（<c>null</c>／空＝未標示）＋台詞。**尚無時間**——時間由 <see cref="ITranscriptAligner.AlignAsync"/> 對齊 Whisper 後補上。</summary>
public sealed record TranscriptLine(string? Speaker, string Text);

/// <summary>字幕檔整理結果（增量5′）：逐句（說話人＋台詞）序列＋API 用量（供費用顯示、記帳）＋<see cref="Truncated"/>（AI 輸出因上限被截斷＝內容過長、結果不可靠，呼叫端據此給明確錯誤而非靜默）。</summary>
public sealed record TranscriptParseResult(IReadOnlyList<TranscriptLine> Lines, IReadOnlyList<SpeakerUsage> Usages, bool Truncated = false);

/// <summary>對齊結果（增量5′）：<see cref="StartSecs"/> 與輸入 lines 等長、逐句對應之開始秒（<c>null</c>＝對不上／時間未知）＋API 用量。</summary>
public sealed record TranscriptAlignResult(IReadOnlyList<double?> StartSecs, IReadOnlyList<SpeakerUsage> Usages);
