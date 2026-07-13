namespace LingoIsland.Video;

/// <summary>一句字幕：文字＋起訖秒（[modVideoCapture模組] 影片擷取契約，spec#2）。</summary>
public sealed record SubtitleCue(string Text, double StartSec, double EndSec);
