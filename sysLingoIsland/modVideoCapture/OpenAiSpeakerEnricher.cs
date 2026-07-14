using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 以 OpenAI 依台詞逐句推斷說話人（[modVideoCapture模組]／[apiIntf標準OPENAI的API協定]，epic #145 增量6，#156）：
/// 讀 <c>OPENAI_API_KEY</c>（僅環境變數、不落地）、以 json_schema 結構化輸出請模型回逐句說話人，
/// 交 <see cref="SpeakerInference"/> 解析。**推斷來源為台詞文字＋常識、非觀看畫面**。沿用既有查詢之金鑰/端點模式。
/// 無金鑰／HTTP 非 2xx／逾時／連線中斷／解析失敗一律擲 <see cref="SpeakerEnrichException"/>。
/// </summary>
public sealed class OpenAiSpeakerEnricher : ISpeakerEnricher
{
    private readonly string _model;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();

    /// <summary>逐句補全一整份字幕（長片可達數百句）較久，逾時至少放寬到此秒數——沿用 word 查詢的短逾時（如 15s）會誤切長片。</summary>
    private const int MinTimeoutSec = 120;

    public OpenAiSpeakerEnricher(string model, int timeoutSec)
    {
        _model = model;
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<SpeakerEnrichResult> InferSpeakersAsync(
        IReadOnlyList<SubtitleCue> cues, string? videoTitle, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (cues.Count == 0) return new SpeakerEnrichResult(Array.Empty<string?>(), Array.Empty<SpeakerUsage>());

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException(
                "OPENAI_API_KEY environment variable is not set — cannot infer speakers. Set it and restart.");
        }
        progress?.Report("Analyzing the dialogue with AI…");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(BuildPayload(SpeakerInference.BuildPrompt(cues, videoTitle)));

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
            throw new SpeakerEnrichException($"Speaker inference timed out ({_timeoutSec}s).");
        }
        catch (HttpRequestException ex)
        {
            throw new SpeakerEnrichException("Network error while inferring speakers: " + ex.Message);
        }

        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SpeakerEnrichException($"OpenAI responded {(int)resp.StatusCode} while inferring speakers.");
            }
            try
            {
                var usage = (SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0)) with { Model = _model, WebSearch = false };
                return new SpeakerEnrichResult(SpeakerInference.ParseSpeakers(json), new[] { usage });
            }
            catch (Exception ex)
            {
                throw new SpeakerEnrichException("Could not parse speaker-inference response: " + ex.Message);
            }
        }
    }

    /// <summary>結構化輸出 payload：回 <c>{"speakers":[string,…]}</c>（陣列長度由模型依句數對齊，疊加端界限安全）。</summary>
    private object BuildPayload(string prompt) => new
    {
        model = _model,
        messages = new object[]
        {
            new { role = "user", content = prompt },
        },
        response_format = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "speaker_labels",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["speakers"] = new { type = "array", items = new { type = "string" } },
                    },
                    required = new[] { "speakers" },
                    additionalProperties = false,
                },
            },
        },
    };
}
