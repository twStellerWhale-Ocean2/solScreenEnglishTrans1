namespace LingoIsland.Video;

/// <summary>
/// 影片音訊轉錄為逐句字幕（[modVideoCapture模組]／[techItem字幕擷取] 增量：Whisper ASR，#187）：抓聲音檔直接語音轉文字，
/// 得**與實際發音對齊**的時間軸——修正 YouTube 自動字幕逐字滾動/時間漂移導致的到句暫停不準。回逐句 <see cref="SubtitleCue"/>
/// （start-only、時間為音訊絕對秒）。**會用到 OpenAI 金鑰＋下載音訊**，故一律由使用者按鈕觸發、且跑前確認估算費用（不自動花費）。
/// 無金鑰／下載或轉錄失敗／逾時／取消一律擲 <see cref="TranscribeException"/>（訊息人類可讀）或傳遞 <see cref="OperationCanceledException"/>。
/// </summary>
public interface IAudioTranscriber
{
    /// <summary>
    /// 下載影片音訊、（過長則分塊）送 OpenAI 轉錄、合併為逐句字幕。<paramref name="progress"/> 逐步回報（下載／轉錄第 n 塊…）供進度視窗顯示。
    /// 回逐句字幕＋實際音訊秒數（供依實測時長顯示實際費用，非僅跑前估算）。
    /// </summary>
    Task<TranscribeResult> TranscribeAsync(
        string videoUrlOrId, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>轉錄結果：逐句字幕＋實際音訊秒數（<see cref="AudioSeconds"/> 用於依 Whisper 每分鐘單價算實際費用）。</summary>
public sealed record TranscribeResult(IReadOnlyList<SubtitleCue> Cues, double AudioSeconds);

/// <summary>音訊轉錄失敗（金鑰缺失／yt-dlp 或 ffmpeg 缺失／下載或轉錄非 2xx／逾時／解析失敗）；訊息即人類可讀，UI 直接顯示。</summary>
public sealed class TranscribeException : Exception
{
    public TranscribeException(string message) : base(message) { }
    public TranscribeException(string message, Exception inner) : base(message, inner) { }
}
