using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 以 OpenAI 重分句＋標說話人（[modVideoCapture模組]／[apiIntf標準OPENAI的API協定]，#189 Row2「AI 分析」）：
/// 讀 <c>OPENAI_API_KEY</c>（僅環境變數、不落地）、以 json_schema 結構化輸出請模型把破碎字幕重新併為完整句並標說話人，
/// 交 <see cref="SubtitleRefine"/> 解析＋建 cue（**時間沿用原格、不變**）。沿用既有查詢之金鑰/端點模式。
/// 無金鑰／HTTP 非 2xx／逾時／連線中斷／解析失敗一律擲 <see cref="RefineException"/>。
/// </summary>
public sealed class OpenAiRefiner : ISubtitleRefiner
{
    private readonly string _model;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();

    /// <summary>重分句一整份字幕（長片數百句）較久，逾時下限放寬（沿用說話人推斷慣例）。</summary>
    private const int MinTimeoutSec = 120;

    public OpenAiRefiner(string model, int timeoutSec)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<RefineResult> RefineAsync(
        IReadOnlyList<SubtitleCue> cues, string? videoTitle, IProgress<string>? progress = null,
        CancellationToken ct = default, string? videoTheme = null)
    {
        if (cues.Count == 0) { return new RefineResult(Array.Empty<RefinedSegment>(), Array.Empty<SpeakerUsage>()); }

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new RefineException("OPENAI_API_KEY environment variable is not set — cannot refine subtitles. Set it and restart.");
        }
        progress?.Report("Re-segmenting the subtitles with AI…");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(BuildPayload(SubtitleRefine.BuildPrompt(cues, videoTitle, videoTheme)));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new RefineException($"Subtitle refine timed out ({_timeoutSec}s)."); }
        catch (HttpRequestException ex) { throw new RefineException("Network error while refining subtitles: " + ex.Message); }

        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new RefineException($"OpenAI responded {(int)resp.StatusCode} while refining subtitles.");
            }
            try
            {
                var usage = (SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0)) with { Model = _model, WebSearch = false };
                return new RefineResult(SubtitleRefine.ParseSegments(json), new[] { usage });
            }
            catch (Exception ex)
            {
                throw new RefineException("Could not parse subtitle-refine response: " + ex.Message);
            }
        }
    }

    /// <summary>結構化輸出 payload：回 <c>{"segments":[{"startIndex":int,"text":str,"speaker":str}]}</c>（strict schema）。</summary>
    private object BuildPayload(string prompt) => new
    {
        model = _model,
        messages = new object[] { new { role = "user", content = prompt } },
        response_format = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "resegmented_subtitles",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["segments"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["startIndex"] = new { type = "integer" },
                                    ["text"] = new { type = "string" },
                                    ["speaker"] = new { type = "string" },
                                },
                                required = new[] { "startIndex", "text", "speaker" },
                                additionalProperties = false,
                            },
                        },
                    },
                    required = new[] { "segments" },
                    additionalProperties = false,
                },
            },
        },
    };
}
