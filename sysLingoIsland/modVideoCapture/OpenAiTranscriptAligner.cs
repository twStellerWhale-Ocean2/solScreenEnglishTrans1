using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 字幕主線之 AI 抽取實作（[modVideoCapture模組]，epic #178 增量6′-B「時間 pivot」定案）：以 OpenAI Responses API（<b>不上網</b>）完成
/// <see cref="ExtractTimedCuesAsync"/>——讀網頁純文字、逐句抽出「時間戳＋說話人＋台詞」（時間照網頁原樣抄、非估算/對齊，故不亂序）。
/// 採 strict json_schema；純提示組建／回應解析在 <see cref="TranscriptAlign"/> 可單元測試。
/// 讀 <c>OPENAI_API_KEY</c>；無金鑰／HTTP 非 2xx／逾時／解析失敗一律擲 <see cref="SpeakerEnrichException"/>；使用者取消傳遞。
/// </summary>
public sealed class OpenAiTranscriptAligner : ITranscriptAligner
{
    private readonly string _parseModel;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();

    private const int MinTimeoutSec = 120;

    public OpenAiTranscriptAligner(string parseModel = "gpt-4.1-mini", int timeoutSec = 120)
    {
        _parseModel = string.IsNullOrWhiteSpace(parseModel) ? "gpt-4.1-mini" : parseModel;   // 由雜訊頁逐句抽時間＋說話人＋台詞，需穩健
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<SubtitleExtractResult> ExtractTimedCuesAsync(
        string rawTranscript, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            return new SubtitleExtractResult(Array.Empty<SubtitleCue>(), Array.Empty<SpeakerUsage>());
        }
        var key = RequireKey();
        progress?.Report("AI 正在讀取頁面並抽取字幕…");
        var (json, usage) = await CallAsync(key, BuildExtractRequest(rawTranscript), ct);
        var usages = new[] { usage with { Model = _parseModel, WebSearch = false } };
        // 頁面過長→輸出被上限截斷（status=incomplete）→ 結果不可靠,回 Truncated 讓呼叫端仍記費用、給明確錯誤。
        var truncated = TranscriptAlign.IsTruncated(json);
        var cues = truncated ? Array.Empty<SubtitleCue>() : TranscriptAlign.ParseExtractedCues(json);
        progress?.Report(truncated
            ? "此頁面太長，無法一次抽取完成。"
            : $"已從頁面抽取 {cues.Count} 句。");
        return new SubtitleExtractResult(cues, usages, truncated);
    }

    private static string RequireKey()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException("尚未設定 OPENAI_API_KEY 環境變數，無法整理或對齊字幕檔。請設定後重新啟動應用程式。");
        }
        return key!;
    }

    private object BuildExtractRequest(string transcriptText) => new
    {
        model = _parseModel, // 無 tools：不上網,只讀給定網頁純文字、逐句抽時間＋說話人＋台詞
        input = TranscriptAlign.BuildExtractPrompt(transcriptText),
        max_output_tokens = 32000, // 整頁逐句較長；放寬上限（仍過長→status=incomplete,由 IsTruncated 偵測給明確錯誤）
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "extracted_cues",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["cues"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["time"] = new { type = "string" },
                                    ["speaker"] = new { type = "string" },
                                    ["text"] = new { type = "string" },
                                },
                                required = new[] { "time", "speaker", "text" },
                                additionalProperties = false,
                            },
                        },
                    },
                    required = new[] { "cues" },
                    additionalProperties = false,
                },
            },
        },
    };

    private const int MaxAttempts = 3; // 暫態失敗（500/502/503/504／429／逾時／連線）退避重試——大請求偶發 500 多為暫態（增量5′ smoke 實測）

    /// <summary>POST <c>/v1/responses</c>，回 (原始 json, 用量)；暫態失敗退避重試，非暫態非 2xx／逾時／連線錯轉可讀失敗（含回應本文摘要供診斷）；使用者取消傳遞。</summary>
    private async Task<(string Json, SpeakerUsage Usage)> CallAsync(string key, object body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) when (attempt < MaxAttempts) { await BackoffAsync(attempt, ct); continue; } // 逾時→退避重試
            catch (OperationCanceledException) { throw new SpeakerEnrichException($"字幕整理／對齊逾時（{_timeoutSec} 秒）。"); }
            catch (HttpRequestException) when (attempt < MaxAttempts) { await BackoffAsync(attempt, ct); continue; }         // 連線錯→退避重試
            catch (HttpRequestException ex) { throw new SpeakerEnrichException("整理／對齊字幕檔時發生網路錯誤：" + ex.Message); }
            using (resp)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    if ((code == 429 || code >= 500) && attempt < MaxAttempts) { await BackoffAsync(attempt, ct); continue; } // 429／5xx＝暫態→退避重試
                    throw new SpeakerEnrichException($"整理／對齊字幕檔時 OpenAI 回應 {code}：" + Truncate(json, 300));
                }
                var usage = SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0);
                return (json, usage);
            }
        }
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct);

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? (s ?? "") : s.Substring(0, n) + "…";
}
