using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 「由字幕檔網址配影片」正式實作（[modVideoCapture模組]，epic #178 增量2〔由逐字稿改造〕，#182）：Responses API＋<c>web_search</c>（gpt-4.1），**兩階段**。
/// 真 API smoke 發現「單次呼叫」對目錄頁（列很多支劇本連結者）只讀目錄本身、不鑽進各劇本連結（回 0 支），USR 拍板改：
/// <b>第1階段（列連結）</b>——打開使用者所貼網址，單檔頁回其自身、目錄頁列各字幕檔連結（去重濾失效，最多 max）；
/// <b>第2階段（逐支驗證＋配片）</b>——對第1階段每個連結各發一次 web_search，打開該份、驗證含說話人（時間可有可無）、合格者配對 YouTube，回其標題／來源／原始 URL。
/// 各階段 strict json_schema（第1階段 links 陣列 min/maxItems 防 web_search＋strict 退化崩潰——專案 memory 硬規範；第2階段單一物件、無陣列故無需 min/maxItems）。web_search 需 gpt-4.1（非 mini）。
/// Usages 累加所有階段之 token（各筆保留 Model／WebSearch 供記帳）；找滿 max 支合格即停（省 API）。
/// 讀 <c>OPENAI_API_KEY</c>；無金鑰／HTTP 非 2xx／逾時／解析失敗一律擲 <see cref="SpeakerEnrichException"/>。實際影片定位（yt-dlp）／篩可載入由 UI 續做。
/// </summary>
public sealed class OpenAiTranscriptVideoFinder : ITranscriptVideoFinder
{
    private readonly string _model;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();
    private const int MinTimeoutSec = 120;
    private const int MaxOutputTokens = 8000; // 兩階段各 call 之輸出上限（僅上限、非預留）：留足 sample 原文／連結清單免截斷

    public OpenAiTranscriptVideoFinder(string model, int timeoutSec)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4.1" : model; // web_search 需支援之模型（非 mini）
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<TranscriptVideoFindResult> FindAsync(
        string subtitleUrl, int max, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null)
    {
        if (string.IsNullOrWhiteSpace(subtitleUrl))
        {
            return new TranscriptVideoFindResult(Array.Empty<TranscriptVideoFind.Candidate>(), Array.Empty<SpeakerUsage>());
        }

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException("OPENAI_API_KEY environment variable is not set — cannot read the subtitle page. Set it and restart.");
        }

        max = Math.Clamp(max, 1, 20);
        var url = subtitleUrl.Trim();
        var usages = new List<SpeakerUsage>();

        // ── 第 1 階段：列連結（單檔頁→1 筆自身；目錄頁→逐份字幕檔連結，去重濾失效，最多 max） ──
        progress?.Report($"Reading the subtitle page “{url}” and listing transcript links…");
        var (listJson, listUsage) = await CallAsync(key!, BuildListLinksRequest(url, max), ct);
        usages.Add(listUsage with { Model = _model, WebSearch = true });
        var links = TranscriptVideoFind.ParseTranscriptLinks(listJson, url); // 傳入原網址：單檔頁缺 transcript_url 時退用之
        if (links.Count == 0)
        {
            progress?.Report("No transcript link was found on that page — check the URL, or try an index page that lists transcripts.");
            return new TranscriptVideoFindResult(Array.Empty<TranscriptVideoFind.Candidate>(), usages);
        }

        // ── 第 2 階段：逐支驗證含說話人＋配片（找滿 max 支合格即停，省 API） ──
        var candidates = new List<TranscriptVideoFind.Candidate>();
        var total = Math.Min(links.Count, max);
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (title, transcriptUrl) = links[i];
            progress?.Report($"Checking transcript {i + 1}/{total}…");
            var (oneJson, oneUsage) = await CallAsync(key!, BuildValidateOneRequest(transcriptUrl, title, videoTheme), ct);
            usages.Add(oneUsage with { Model = _model, WebSearch = true });
            var candidate = TranscriptVideoFind.ParseOneCandidate(oneJson, transcriptUrl);
            if (candidate is not null)
            {
                candidates.Add(candidate);
                if (candidates.Count >= max) { break; } // 找滿 max 支合格即停
            }
        }

        progress?.Report(candidates.Count > 0
            ? $"Matched {candidates.Count} video(s) whose transcript shows speakers."
            : "No transcript with a visible speaker was matched to a video on that page.");
        return new TranscriptVideoFindResult(candidates, usages);
    }

    /// <summary>第1階段請求——列連結。strict json_schema：<c>{links:[{title, transcript_url}]}</c>，links 陣列 min/maxItems（防 web_search＋strict 退化崩潰）、各欄 required、additionalProperties=false。</summary>
    private object BuildListLinksRequest(string subtitleUrl, int max) => new
    {
        model = _model,
        tools = new object[] { new { type = "web_search" } },
        input = TranscriptVideoFind.BuildListLinksPrompt(subtitleUrl, max),
        max_output_tokens = MaxOutputTokens,
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "transcript_links",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["links"] = new
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
                                    ["transcript_url"] = new { type = "string" },
                                },
                                required = new[] { "title", "transcript_url" },
                                additionalProperties = false,
                            },
                        },
                    },
                    required = new[] { "links" },
                    additionalProperties = false,
                },
            },
        },
    };

    /// <summary>第2階段請求——驗證單份＋配片。strict json_schema：單一物件 <c>{title, youtube_url, source, transcript_url, has_speaker, sample}</c>（無陣列、故不需 min/maxItems），各欄 required、additionalProperties=false。</summary>
    private object BuildValidateOneRequest(string transcriptUrl, string? title, string? videoTheme) => new
    {
        model = _model,
        tools = new object[] { new { type = "web_search" } },
        input = TranscriptVideoFind.BuildValidateOnePrompt(transcriptUrl, title, videoTheme),
        max_output_tokens = MaxOutputTokens,
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "transcript_one",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["title"] = new { type = "string" },
                        ["youtube_url"] = new { type = "string" },
                        ["source"] = new { type = "string" },
                        ["transcript_url"] = new { type = "string" },
                        ["has_speaker"] = new { type = "boolean" },
                        ["sample"] = new { type = "string" },
                    },
                    required = new[] { "title", "youtube_url", "source", "transcript_url", "has_speaker", "sample" },
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
        catch (OperationCanceledException) { throw new SpeakerEnrichException($"Reading the subtitle page timed out ({_timeoutSec}s)."); }
        catch (HttpRequestException ex) { throw new SpeakerEnrichException("Network error while reading the subtitle page: " + ex.Message); }
        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SpeakerEnrichException(
                    $"OpenAI responded {(int)resp.StatusCode} while reading the subtitle page — web search needs a supported model (e.g. gpt-4.1) and Responses API access.");
            }
            var usage = SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0);
            return (json, usage);
        }
    }
}
