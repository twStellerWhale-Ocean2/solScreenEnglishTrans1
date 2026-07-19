using System.Net.Http;
using System.Net.Http.Json;

namespace LingoIsland.Video;

/// <summary>
/// 字幕主線之 AI 對齊實作（[modVideoCapture模組]，epic #178 增量5′）：以 OpenAI Responses API（<b>不上網</b>）完成
/// (1) <see cref="ParseTranscriptAsync"/> 把字幕檔原文整理成逐句（說話人＋台詞）；(2) <see cref="AlignAsync"/> 逐塊把台詞對齊到 Whisper 聲音時間軸取時間。
/// 採 find→align 骨架（strict json_schema、逐塊等長、混模型記帳），但**語意反轉**：align 回「每句時間」而非說話人。
/// parse 用能穩健擷取之模型（gpt-4.1-mini）、align 用便宜模型（gpt-4o-mini）；純提示組建／回應解析在 <see cref="TranscriptAlign"/> 可單元測試。
/// 讀 <c>OPENAI_API_KEY</c>；無金鑰／HTTP 非 2xx／逾時／解析失敗一律擲 <see cref="SpeakerEnrichException"/>；使用者取消傳遞。
/// </summary>
public sealed class OpenAiTranscriptAligner : ITranscriptAligner
{
    private readonly string _parseModel;
    private readonly string _alignModel;
    private readonly int _timeoutSec;
    private static readonly HttpClient Http = new();

    private const int MinTimeoutSec = 120;

    public OpenAiTranscriptAligner(string parseModel = "gpt-4.1-mini", string alignModel = "gpt-4o-mini", int timeoutSec = 120)
    {
        _parseModel = string.IsNullOrWhiteSpace(parseModel) ? "gpt-4.1-mini" : parseModel;   // 由雜訊頁擷取對白＋說話人，需穩健
        _alignModel = string.IsNullOrWhiteSpace(alignModel) ? "gpt-4o-mini" : alignModel;    // 對照時間軸標時間，便宜足矣
        _timeoutSec = Math.Max(timeoutSec, MinTimeoutSec);
    }

    public async Task<TranscriptParseResult> ParseTranscriptAsync(
        string rawTranscript, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            return new TranscriptParseResult(Array.Empty<TranscriptLine>(), Array.Empty<SpeakerUsage>());
        }
        var key = RequireKey();

        progress?.Report("Organizing the subtitle file into speaker lines…");
        var (json, usage) = await CallAsync(key, BuildParseRequest(rawTranscript), ct);
        var usages = new[] { usage with { Model = _parseModel, WebSearch = false } };
        // 審查修：輸出被上限截斷（status=incomplete）＝字幕檔過長、整理結果不可靠（截斷 JSON 解析多為空）。
        // 回 Truncated 旗標讓呼叫端仍記已花之 parse 費用、並給「內容過長」明確錯誤，而非靜默回空、誤指 URL、反覆付費重試。
        var truncated = TranscriptAlign.IsTruncated(json);
        var lines = truncated ? Array.Empty<TranscriptLine>() : TranscriptAlign.ParseLines(json);
        progress?.Report(truncated
            ? "The subtitle file is too long to organize in one pass."
            : $"Organized {lines.Count} line(s) from the subtitle file.");
        return new TranscriptParseResult(lines, usages, truncated);
    }

    public async Task<TranscriptAlignResult> AlignAsync(
        IReadOnlyList<TranscriptLine> lines, IReadOnlyList<SubtitleCue> audioCues,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (lines.Count == 0)
        {
            return new TranscriptAlignResult(Array.Empty<double?>(), Array.Empty<SpeakerUsage>());
        }
        var key = RequireKey();

        // 精度修：以「段編號」對齊——模型挑每句對應之聲音段編號，時間取該段之 Whisper **精確時間**（非模型估算，消除 ±數秒抖動）。
        var segs = TranscriptAlign.UsableAudioSegments(audioCues);
        var timeline = TranscriptAlign.RenderAudioTimeline(segs);
        var startSecs = new double?[lines.Count];
        var usages = new List<SpeakerUsage>();
        var chunkCount = (lines.Count + TranscriptAlign.ChunkSize - 1) / TranscriptAlign.ChunkSize;
        for (var ci = 0; ci < chunkCount; ci++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = ci * TranscriptAlign.ChunkSize;
            var chunk = lines.Skip(offset).Take(TranscriptAlign.ChunkSize).ToList();
            progress?.Report($"Aligning lines {offset + 1}–{offset + chunk.Count} of {lines.Count} to the audio…");
            var (json, usage) = await CallAsync(key, BuildAlignRequest(timeline, chunk), ct);
            usages.Add(usage with { Model = _alignModel, WebSearch = false });
            var refs = TranscriptAlign.ParseRefs(json, chunk.Count);
            var times = TranscriptAlign.MapRefsToTimes(refs, segs);
            for (var j = 0; j < chunk.Count && offset + j < startSecs.Length; j++)
            {
                startSecs[offset + j] = j < times.Count ? times[j] : null;
            }
        }
        return new TranscriptAlignResult(startSecs, usages);
    }

    public async Task<SubtitleExtractResult> ExtractTimedCuesAsync(
        string rawTranscript, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            return new SubtitleExtractResult(Array.Empty<SubtitleCue>(), Array.Empty<SpeakerUsage>());
        }
        var key = RequireKey();
        progress?.Report("AI is reading the page and extracting the subtitle…");
        var (json, usage) = await CallAsync(key, BuildExtractRequest(rawTranscript), ct);
        var usages = new[] { usage with { Model = _parseModel, WebSearch = false } };
        // 頁面過長→輸出被上限截斷（status=incomplete）→ 結果不可靠,回 Truncated 讓呼叫端仍記費用、給明確錯誤（同 ParseTranscriptAsync）。
        var truncated = TranscriptAlign.IsTruncated(json);
        var cues = truncated ? Array.Empty<SubtitleCue>() : TranscriptAlign.ParseExtractedCues(json);
        progress?.Report(truncated
            ? "This page is too long to extract in one pass."
            : $"Extracted {cues.Count} line(s) from the page.");
        return new SubtitleExtractResult(cues, usages, truncated);
    }

    private static string RequireKey()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new SpeakerEnrichException("OPENAI_API_KEY environment variable is not set — cannot organize or align the subtitle file. Set it and restart.");
        }
        return key!;
    }

    private object BuildParseRequest(string rawTranscript) => new
    {
        model = _parseModel, // 無 tools：不上網，只整理給定原文
        input = TranscriptAlign.BuildParsePrompt(rawTranscript),
        max_output_tokens = 32000, // 整份逐句序列較長；放寬上限涵蓋一集（審查修：仍過長→回應 status=incomplete，由 IsTruncated 偵測給明確錯誤）
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "transcript_lines",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["lines"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["speaker"] = new { type = "string" },
                                    ["text"] = new { type = "string" },
                                },
                                required = new[] { "speaker", "text" },
                                additionalProperties = false,
                            },
                        },
                    },
                    required = new[] { "lines" },
                    additionalProperties = false,
                },
            },
        },
    };

    private object BuildAlignRequest(string timeline, IReadOnlyList<TranscriptLine> chunk) => new
    {
        model = _alignModel, // 無 tools：不上網，只對照已編號聲音段
        input = TranscriptAlign.BuildAlignPrompt(chunk, timeline),
        max_output_tokens = chunk.Count * 8 + 400,
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "line_refs",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["refs"] = new { type = "array", items = new { type = "integer" }, minItems = chunk.Count, maxItems = chunk.Count },
                    },
                    required = new[] { "refs" },
                    additionalProperties = false,
                },
            },
        },
    };

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
            catch (OperationCanceledException) { throw new SpeakerEnrichException($"Subtitle organizing/aligning timed out ({_timeoutSec}s)."); }
            catch (HttpRequestException) when (attempt < MaxAttempts) { await BackoffAsync(attempt, ct); continue; }         // 連線錯→退避重試
            catch (HttpRequestException ex) { throw new SpeakerEnrichException("Network error while organizing/aligning the subtitle file: " + ex.Message); }
            using (resp)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    if ((code == 429 || code >= 500) && attempt < MaxAttempts) { await BackoffAsync(attempt, ct); continue; } // 429／5xx＝暫態→退避重試
                    throw new SpeakerEnrichException($"OpenAI responded {code} while organizing/aligning the subtitle file: " + Truncate(json, 300));
                }
                var usage = SpeakerInference.ParseUsage(json) ?? new SpeakerUsage(0, 0, 0);
                return (json, usage);
            }
        }
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct);

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? (s ?? "") : s.Substring(0, n) + "…";
}
