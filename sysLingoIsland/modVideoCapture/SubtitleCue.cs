namespace LingoIsland.Video;

/// <summary>
/// 一句字幕：文字＋<b>開始秒</b>＋（可選）說話人（[modVideoCapture模組] 影片擷取契約，spec#2；說話人＝增量5；
/// start-only＝#158）。<b>只留開始時間</b>——一句自 <see cref="StartSec"/> 顯示到<b>下一句的開始</b>（無空窗），
/// 到句暫停於「下一句開始，或本句開始＋上限（防超長間隔乾等）」觸發（見 <see cref="PauseDecider"/>）。
/// 結束時間不入對外模型：解析階段（json3 併句／VTT 去滾動）內部以 <see cref="TimedCue"/> 保留、不外露。
/// <paramref name="Speaker"/> null／空＝未標示；有值＝取自 VTT <c>&lt;v Name&gt;</c> 語音標記或使用者 YAML 編修標註。
/// </summary>
public sealed record SubtitleCue(string Text, double StartSec, string? Speaker = null);

/// <summary>解析階段內部用之含結束時間 cue（json3 併句之間隔判斷、VTT 去滾動需要）；不對外、轉 <see cref="SubtitleCue"/> 時丟棄 <see cref="EndSec"/>。</summary>
internal sealed record TimedCue(string Text, double StartSec, double EndSec, string? Speaker = null);
