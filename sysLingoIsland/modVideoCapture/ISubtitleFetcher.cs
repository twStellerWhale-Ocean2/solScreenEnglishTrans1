namespace LingoIsland.Video;

/// <summary>
/// 影片字幕取得（[techItem字幕擷取]，spec#2）：抽介面供測試以假實作注入、不打真網路／不起 CLI；
/// 正式實作見 <see cref="YtDlpSubtitleFetcher"/>。**僅取字幕文字、不下載影片內容。**
/// </summary>
public interface ISubtitleFetcher
{
    /// <summary>取指定 YouTube 影片之英文字幕逐句 cue；無字幕／取得失敗擲 <see cref="SubtitleException"/>。</summary>
    Task<IReadOnlyList<SubtitleCue>> FetchAsync(string videoUrlOrId, CancellationToken ct = default);
}

/// <summary>字幕取得之明確可讀失敗（無字幕、yt-dlp 缺失、私人／無效影片、逾時等）——中止該片、不當機不無聲失敗。</summary>
public sealed class SubtitleException : Exception
{
    public SubtitleException(string message) : base(message) { }
}
