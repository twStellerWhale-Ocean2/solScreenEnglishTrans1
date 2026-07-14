using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 以 OpenAI【網路搜尋工具】上網找該影集逐字稿/角色資料、逐句補說話人（[modVideoCapture模組]，epic #145 增量6b，#145 §D 第二來源）：
/// 走 <b>Responses API</b>（<c>/v1/responses</c>）＋ <c>web_search</c> 工具——模型真的上網、依熱門可信來源判斷「誰說的」，
/// 交 <see cref="SpeakerInference.ParseWebSpeakers"/> 解析、<see cref="SpeakerInference.MergeSpeakers"/> 非破壞疊加。
/// <b>字幕文字/時間仍為 yt-dlp、本來源只補說話人</b>（網路逐字稿無逐句時間軸，不取代字幕本體）。
/// 讀 <c>OPENAI_API_KEY</c>（僅環境變數）；預設用支援網搜之模型（<c>gpt-4.1-mini</c>，非查詢用的 gpt-4o-mini）。
/// 無金鑰／HTTP 非 2xx／逾時／連線中斷／解析失敗一律擲 <see cref="SpeakerEnrichException"/>。
/// </summary>
public sealed class OpenAiWebSpeakerEnricher : ISpeakerEnricher
{
    private readonly string _model;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();

    /// <summary>網搜較慢，逾時至少放寬到此秒數（避免沿用查詢用的短逾時把網搜切掉）。</summary>
    private const int MinTimeoutSec = 60;

    public OpenAiWebSpeakerEnricher(string model, int timeoutSec)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4.1-mini" : model; // web_search 需支援之模型
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<SpeakerEnrichResult> InferSpeakersAsync(
        IReadOnlyList<SubtitleCue> cues, string? videoTitle, CancellationToken ct = default)
    {
        if (cues.Count == 0) return new SpeakerEnrichResult(Array.Empty<string?>(), null, _model);

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException(
                "OPENAI_API_KEY environment variable is not set — cannot look up speakers online. Set it and restart.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(new
        {
            model = _model,
            tools = new object[] { new { type = "web_search" } }, // 內建網搜工具（模型自行上網）
            input = SpeakerInference.BuildWebPrompt(cues, videoTitle),
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 使用者主動取消（載入新片等）——傳遞、由 UI 顯示取消
        }
        catch (OperationCanceledException)
        {
            throw new SpeakerEnrichException($"Online speaker lookup timed out ({_timeoutSec}s).");
        }
        catch (HttpRequestException ex)
        {
            throw new SpeakerEnrichException("Network error while looking up speakers online: " + ex.Message);
        }

        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SpeakerEnrichException(
                    $"OpenAI responded {(int)resp.StatusCode} for the online lookup — web search needs a supported model (e.g. gpt-4.1-mini) and Responses API access.");
            }
            try
            {
                return new SpeakerEnrichResult(SpeakerInference.ParseWebSpeakers(json), SpeakerInference.ParseUsage(json), _model);
            }
            catch (Exception ex)
            {
                throw new SpeakerEnrichException("Could not parse the online speaker-lookup response: " + ex.Message);
            }
        }
    }
}
