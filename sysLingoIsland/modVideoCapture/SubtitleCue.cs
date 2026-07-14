namespace LingoIsland.Video;

/// <summary>
/// 一句字幕：文字＋起訖秒＋（可選）說話人（[modVideoCapture模組] 影片擷取契約，spec#2；說話人＝epic #145 增量5）。
/// <paramref name="Speaker"/> null／空＝未標示（多數自動字幕、無語音標記之人工字幕）；有值＝取自 VTT <c>&lt;v Name&gt;</c>
/// 語音標記，或使用者於整檔 YAML 編修時標註。以第四位選用參數加入，既有 <c>new SubtitleCue(text, start, end)</c> 呼叫不受影響。
/// </summary>
public sealed record SubtitleCue(string Text, double StartSec, double EndSec, string? Speaker = null);
