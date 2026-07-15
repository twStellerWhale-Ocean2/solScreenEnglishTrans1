using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 以「上網找逐字稿 → 逐塊對齊」管線補說話人（[modVideoCapture模組]，epic #145 增量6b 重做，#145 §D）：
/// (1) 用 Responses API＋<c>web_search</c> 找一份公評良好、完整的逐字稿（模型自評不佳則換源重找，最多 3 次）；
/// (2) 找到後把 yt-dlp 逐句切塊、逐塊給模型「**對照逐字稿**標說話人」（<b>不再上網</b>、用便宜模型）；(3) 組合。
/// 這樣網搜只做一次、逐塊對齊各是小而準的任務——解決「整份一次送、對不上又貴」的長片問題。
/// find 用能上網之強模型（gpt-4.1）、align 用便宜模型（gpt-4o-mini）；各步經 <c>progress</c> 回報、混模型用量各記一筆。
/// <b>字幕文字/時間仍為 yt-dlp、本來源只補說話人</b>。讀 <c>OPENAI_API_KEY</c>；無金鑰／HTTP 非 2xx／逾時／解析失敗一律擲 <see cref="SpeakerEnrichException"/>。
/// </summary>
public sealed class OpenAiWebSpeakerEnricher : ISpeakerEnricher, IWebTranscriptProbe
{
    private readonly string _findModel;
    private readonly string _alignModel;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();

    private const int ChunkSize = 45;
    private const int MaxFindTries = 3;
    private const int MinTimeoutSec = 120;

    public OpenAiWebSpeakerEnricher(string findModel, string alignModel, int timeoutSec)
    {
        _findModel = string.IsNullOrWhiteSpace(findModel) ? "gpt-4.1" : findModel;         // web_search 需支援之模型
        _alignModel = string.IsNullOrWhiteSpace(alignModel) ? "gpt-4o-mini" : alignModel;  // 對齊便宜、有逐字稿可對
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<SpeakerEnrichResult> InferSpeakersAsync(
        IReadOnlyList<SubtitleCue> cues, string? videoTitle, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null)
    {
        if (cues.Count == 0) { return new SpeakerEnrichResult(Array.Empty<string?>(), Array.Empty<SpeakerUsage>()); }

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException("OPENAI_API_KEY environment variable is not set — cannot look up speakers online. Set it and restart.");
        }

        var usages = new List<SpeakerUsage>();

        // ── 步驟 1–2：上網找完整逐字稿（自評不佳→換源重找，最多 3 次） ──
        SpeakerInference.TranscriptFind? found = null;
        for (var attempt = 1; attempt <= MaxFindTries && found is null; attempt++)
        {
            progress?.Report(attempt == 1
                ? "Searching the web for a reputable, complete transcript…"
                : $"That transcript wasn't usable — searching a different source ({attempt}/{MaxFindTries})…");
            var (json, usage) = await CallAsync(key!, BuildFindRequest(videoTitle, attempt > 1, videoTheme), ct);
            usages.Add(usage with { Model = _findModel, WebSearch = true });
            var find = SpeakerInference.ParseFindResult(json);
            if (find.Found && find.Complete && find.Transcript.Trim().Length >= 40)
            {
                found = find;
                progress?.Report($"Found a transcript ({Truncate(find.Source, 60)}). Aligning {cues.Count} subtitle lines…");
            }
        }

        if (found is null)
        {
            progress?.Report("No reliable, complete transcript found online — leaving speakers unchanged.");
            return new SpeakerEnrichResult(new string?[cues.Count], usages); // 全 null：非破壞疊加時不動任何句
        }

        // ── 步驟 3–4：切塊、逐塊「對照逐字稿」標說話人（不上網、便宜模型） ──
        var speakers = new string?[cues.Count];
        var chunks = (cues.Count + ChunkSize - 1) / ChunkSize;
        for (var ci = 0; ci < chunks; ci++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = ci * ChunkSize;
            var chunk = cues.Skip(offset).Take(ChunkSize).ToList();
            progress?.Report($"Aligning chunk {ci + 1}/{chunks} (lines {offset + 1}–{offset + chunk.Count})…");
            var (json, usage) = await CallAsync(key!, BuildAlignRequest(found.Transcript, chunk), ct);
            usages.Add(usage with { Model = _alignModel, WebSearch = false });
            var labels = SpeakerInference.ParseWebSpeakers(json);
            for (var j = 0; j < chunk.Count && offset + j < speakers.Length; j++)
            {
                speakers[offset + j] = j < labels.Count ? labels[j] : null;
            }
        }

        progress?.Report("Combining the aligned chunks…");
        return new SpeakerEnrichResult(speakers, usages);
    }

    /// <summary>
    /// 網路逐字稿可用性探測（#177）：只跑管線第一步「find」——一次 <c>web_search</c> 找逐字稿、回是否找到＋來源，
    /// <b>不做逐塊對齊</b>（故單次、便宜）。供搜尋結果表格「網路字幕」欄按需查。判定「有」＝模型回報 found 且逐字稿內文 ≥40 字。
    /// </summary>
    public async Task<WebTranscriptProbeResult> ProbeAsync(
        string? videoTitle, IProgress<string>? progress = null, CancellationToken ct = default, string? videoTheme = null)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException("OPENAI_API_KEY environment variable is not set — cannot check web subtitles. Set it and restart.");
        }

        progress?.Report("Searching the web for a transcript…");
        var (json, usage) = await CallAsync(key!, BuildFindRequest(videoTitle, retry: false, videoTheme), ct);
        var usages = new[] { usage with { Model = _findModel, WebSearch = true } };
        var find = SpeakerInference.ParseFindResult(json);
        var found = find.Found && find.Transcript.Trim().Length >= 40;
        progress?.Report(found
            ? $"Found a web transcript ({Truncate(find.Source, 60)})."
            : "No usable web transcript found for this video.");
        return new WebTranscriptProbeResult(found, find.Source ?? "", usages);
    }

    private object BuildFindRequest(string? videoTitle, bool retry, string? videoTheme = null) => new
    {
        model = _findModel,
        tools = new object[] { new { type = "web_search" } },
        input = SpeakerInference.BuildFindTranscriptPrompt(videoTitle, retry ? "找不同的來源" : null, videoTheme),
        max_output_tokens = 8000, // 逐字稿全文較長
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "transcript_find",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["found"] = new { type = "boolean" },
                        ["source"] = new { type = "string" },
                        ["complete"] = new { type = "boolean" },
                        ["transcript"] = new { type = "string" },
                    },
                    required = new[] { "found", "source", "complete", "transcript" },
                    additionalProperties = false,
                },
            },
        },
    };

    private object BuildAlignRequest(string transcript, IReadOnlyList<SubtitleCue> chunk) => new
    {
        model = _alignModel, // 無 tools：不上網，只對照逐字稿
        input = SpeakerInference.BuildAlignPrompt(transcript, chunk),
        max_output_tokens = chunk.Count * 15 + 400,
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "speaker_labels",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["speakers"] = new { type = "array", items = new { type = "string" }, minItems = chunk.Count, maxItems = chunk.Count },
                    },
                    required = new[] { "speakers" },
                    additionalProperties = false,
                },
            },
        },
    };

    /// <summary>POST <c>/v1/responses</c>，回 (原始 json, 用量)；非 2xx／逾時／連線錯轉可讀失敗；使用者取消傳遞。</summary>
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
        catch (OperationCanceledException) { throw new SpeakerEnrichException($"Online speaker lookup timed out ({_timeoutSec}s)."); }
        catch (HttpRequestException ex) { throw new SpeakerEnrichException("Network error while looking up speakers online: " + ex.Message); }
        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SpeakerEnrichException(
                    $"OpenAI responded {(int)resp.StatusCode} during the online lookup — web search needs a supported model (e.g. gpt-4.1) and Responses API access.");
            }
            var usage = SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0);
            return (json, usage);
        }
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
}
