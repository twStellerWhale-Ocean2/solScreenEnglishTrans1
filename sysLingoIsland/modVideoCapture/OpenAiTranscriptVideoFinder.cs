using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 「由逐字稿找影片」正式實作（[modVideoCapture模組]，#189 獲得頁重構）：Responses API＋<c>web_search</c>（gpt-4.1）找「該主題**有公開逐字稿可用**」之 YouTube 影片。
/// strict json_schema（videos 陣列 min/maxItems，防 web_search＋strict 之退化崩潰——比照網搜補說話人 6b）；只做「找影片＋逐字稿來源」一步、
/// <b>不下載逐字稿全文</b>（那是載入後補說話人時才逐塊對齊），故單次、相對便宜。web_search 需 gpt-4.1（非 mini）。
/// 讀 <c>OPENAI_API_KEY</c>；無金鑰／HTTP 非 2xx／逾時／解析失敗一律擲 <see cref="SpeakerEnrichException"/>。
/// </summary>
public sealed class OpenAiTranscriptVideoFinder : ITranscriptVideoFinder
{
    private readonly string _model;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();
    private const int MinTimeoutSec = 120;

    public OpenAiTranscriptVideoFinder(string model, int timeoutSec)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4.1" : model; // web_search 需支援之模型（非 mini）
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<TranscriptVideoFindResult> FindAsync(
        string topic, int max, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return new TranscriptVideoFindResult(Array.Empty<TranscriptVideoFind.Candidate>(), Array.Empty<SpeakerUsage>());
        }

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException("OPENAI_API_KEY environment variable is not set — cannot search for videos with transcripts. Set it and restart.");
        }

        max = Math.Clamp(max, 1, 20);
        progress?.Report($"Searching the web for videos about “{topic.Trim()}” that have transcripts…");
        var (json, usage) = await CallAsync(key!, BuildRequest(topic, max, videoTheme), ct);
        var usages = new[] { usage with { Model = _model, WebSearch = true } };
        var candidates = TranscriptVideoFind.ParseCandidates(json);
        progress?.Report(candidates.Count > 0
            ? $"Found {candidates.Count} candidate video(s) with transcripts."
            : "No videos with a usable transcript found for that topic.");
        return new TranscriptVideoFindResult(candidates, usages);
    }

    private object BuildRequest(string topic, int max, string? videoTheme) => new
    {
        model = _model,
        tools = new object[] { new { type = "web_search" } },
        input = TranscriptVideoFind.BuildFindVideosPrompt(topic, max, videoTheme),
        max_output_tokens = 4000,
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "transcript_videos",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["videos"] = new
                        {
                            type = "array",
                            minItems = 0,
                            maxItems = max,
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["title"] = new { type = "string" },
                                    ["youtube_url"] = new { type = "string" },
                                    ["source"] = new { type = "string" },
                                },
                                required = new[] { "title", "youtube_url", "source" },
                                additionalProperties = false,
                            },
                        },
                    },
                    required = new[] { "videos" },
                    additionalProperties = false,
                },
            },
        },
    };

    /// <summary>POST <c>/v1/responses</c>，回 (原始 json, 用量)；非 2xx／逾時／連線錯轉可讀失敗；使用者取消傳遞（比照 <see cref="OpenAiWebSpeakerEnricher"/>）。</summary>
    private async Task<(string Json, SpeakerUsage Usage)> CallAsync(string key, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(body);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SpeakerEnrichException($"Video transcript search timed out ({_timeoutSec}s)."); }
        catch (HttpRequestException ex) { throw new SpeakerEnrichException("Network error while searching for videos with transcripts: " + ex.Message); }
        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SpeakerEnrichException(
                    $"OpenAI responded {(int)resp.StatusCode} during the video search — web search needs a supported model (e.g. gpt-4.1) and Responses API access.");
            }
            var usage = SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0);
            return (json, usage);
        }
    }
}
