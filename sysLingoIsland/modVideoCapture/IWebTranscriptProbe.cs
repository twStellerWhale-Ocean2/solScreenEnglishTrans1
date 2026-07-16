namespace LingoIsland.Video;

/// <summary>
/// 網路逐字稿可用性探測（[modVideoCapture模組]，#177 搜尋結果表格「網路字幕」欄）：只做說話人管線的第一步「find」——
/// 用 Responses API＋<c>web_search</c> 找一份逐字稿、回是否找到＋來源，<b>不做逐塊對齊</b>，故只一次網搜、便宜。
/// 按鈕觸發、逐列按需查（會花 OpenAI 額度）；正式實作見 <see cref="OpenAiWebSpeakerEnricher"/>（與網搜補說話人同一顆）。
/// </summary>
public interface IWebTranscriptProbe
{
    /// <summary>探測是否有可用之網路逐字稿（可選 <paramref name="videoTheme"/> 所屬主題縮小搜尋）；無金鑰／HTTP 非 2xx／逾時／解析失敗擲 <see cref="SpeakerEnrichException"/>。</summary>
    Task<WebTranscriptProbeResult> ProbeAsync(string? videoTitle, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null);
}

/// <summary>網路逐字稿探測結果（#177）：<see cref="Found"/> 是否找到可用逐字稿、<see cref="Source"/> 來源描述（如「PAW Patrol Wiki (Fandom)」）、<see cref="Usages"/> 供費用顯示。</summary>
public sealed record WebTranscriptProbeResult(bool Found, string Source, IReadOnlyList<SpeakerUsage> Usages);
