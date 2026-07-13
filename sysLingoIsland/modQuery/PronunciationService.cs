using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>發音評分結果（[techItem發音評分]，spec#10）：Score 0–100（越高越準）、可選一句繁中建議。</summary>
public sealed record PronunciationResult(int Score, string Note = "")
{
    /// <summary>是否通過（分數達門檻）。</summary>
    public bool IsPass(int threshold) => Score >= threshold;
}

/// <summary>
/// 發音評分抽象（[modQuery模組] 發音評分契約，spec#10）——介面化使單元測試可注入假評分、不打真網路、
/// NotesPage 之發音練習藉此送分。與 [techItem語音合成] 之 TTS 輸出責任區隔。
/// </summary>
public interface IPronunciationAssessor
{
    /// <summary>將錄音（WAV bytes）與目標英文文字送評分，回 0–100 分；永久性失敗擲 <see cref="QueryException"/>。</summary>
    Task<PronunciationResult> AssessAsync(byte[] wavBytes, string targetText, CancellationToken ct = default);
}

/// <summary>
/// OpenAI 音訊輸入模型之發音評分實作（[techItem發音評分]，spec#10）：以 chat completions <c>input_audio</c>
/// 內容型別＋structured output 送錄音與目標句、回發音分數。讀 <c>OPENAI_API_KEY</c>（僅環境變數、不落地），
/// 沿用查詢層之逾時／有限次指數退避重試／降級（暫時性 429/5xx/逾時重試、永久性 4xx/解析失敗不重試、
/// 使用者取消不重試）。解析與門檻比較為純函式、可單元測試。
/// </summary>
public sealed class PronunciationService : IPronunciationAssessor
{
    /// <summary>預設發音評分模型（須支援音訊輸入）。</summary>
    public const string DefaultModel = "gpt-audio-1.5";

    private readonly string _model;
    private readonly int _timeoutSec;
    private readonly int _maxRetries;
    private static readonly HttpClient Http = new();

    public PronunciationService(string model, int timeoutSec, int maxRetries = 2)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _timeoutSec = timeoutSec;
        _maxRetries = Math.Max(0, maxRetries); // 負值視為不重試
    }

    public async Task<PronunciationResult> AssessAsync(byte[] wavBytes, string targetText, CancellationToken ct = default)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new QueryException("OPENAI_API_KEY environment variable is not set; cannot score pronunciation.");
        }
        if (wavBytes is null || wavBytes.Length == 0)
        {
            throw new QueryException("No audio to score.");
        }
        var b64 = Convert.ToBase64String(wavBytes);
        string json;
        try
        {
            json = await RunWithRetryAsync(c => SendOnceAsync(BuildPayload(b64, targetText, structured: true), key, c), ct);
        }
        catch (QueryException ex) when (StructuredOutputUnsupported(ex.Message))
        {
            json = await RunWithRetryAsync(c => SendOnceAsync(BuildPayload(b64, targetText, structured: false), key, c), ct);
        }
        return Parse(json);
    }

    /// <summary>對 attempt 執行有限次指數退避重試（internal 供單元測試注入假 attempt／backoff）。</summary>
    internal async Task<string> RunWithRetryAsync(
        Func<CancellationToken, Task<string>> attempt,
        CancellationToken ct,
        Func<int, CancellationToken, Task>? backoff = null)
    {
        backoff ??= DefaultBackoffAsync;
        for (var i = 0; ; i++)
        {
            try
            {
                return await attempt(ct);
            }
            catch (TransientQueryException ex)
            {
                if (i >= _maxRetries)
                {
                    throw new QueryException(
                        _maxRetries == 0
                            ? $"Pronunciation scoring failed: {ex.Message}"
                            : $"Pronunciation scoring failed after {_maxRetries} retries: {ex.Message}");
                }
                await backoff(i, ct);
            }
        }
    }

    private static Task DefaultBackoffAsync(int attemptIndex, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attemptIndex)), ct);

    private async Task<string> SendOnceAsync(object payload, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 使用者主動取消，不重試
        }
        catch (OperationCanceledException)
        {
            throw new TransientQueryException($"Scoring timed out ({_timeoutSec}s)");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientQueryException("Network connection lost: " + ex.Message);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            if (QueryService.IsTransientStatus(code))
            {
                throw new TransientQueryException($"API transient error {code}");
            }
            throw new QueryException($"API responded {code}: {Truncate(json, 200)}");
        }
        return json;
    }

    /// <summary>
    /// 評分提示（spec#10）：先排除明確無朗讀（靜音／只有雜訊／非人聲／完全無關音訊）才給 0；
    /// 只要可辨識為真人嘗試朗讀目標句或其片段，就依可懂度給非零分，避免口音、童聲或收音品質造成全 0 偽陰性。
    /// internal 供單元測試。
    /// </summary>
    internal const string BasePrompt =
        "你是英語發音評分器。使用者應朗讀下列目標英文句，請評估其發音正確度。"
        + "先判斷音訊是否明確沒有真人朗讀：只有在靜音、只有背景雜訊、非人聲、或完全與目標句無關的音訊時，才給 score=0 並在 note 註明「未偵測到朗讀」。"
        + "若音訊中可聽見真人嘗試朗讀目標英文句、其中一部分、或明顯在模仿該句，即使是兒童聲音、口音很重、發音不準、斷句、漏字、音量小、收音品質差或有背景雜訊，也不可判為未朗讀、不可給 0 分；請依可懂度給 15–100 分。"
        + "評分尺度：100＝接近母語者且清楚自然；80 左右＝大致正確可懂；50–70＝可辨識但有多處音或節奏問題；15–45＝很不準、只聽得出少量目標詞或片段但確有朗讀嘗試。"
        + "請避免過度嚴格；本功能用於兒童練習，目標是區分沒有朗讀與有嘗試朗讀，而不是專業音素鑑定。"
        + "只輸出一個 JSON 物件、不要 markdown 圍欄、不要任何多餘文字，格式為 {\"score\": 整數0到100, \"note\": \"一句簡短繁體中文建議\"}。目標英文句：";

    private static bool StructuredOutputUnsupported(string message)
        => message.Contains("response_format", StringComparison.OrdinalIgnoreCase)
           || message.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
           || message.Contains("schema", StringComparison.OrdinalIgnoreCase);

    /// <summary>組裝評分 payload（input_audio＋JSON schema structured output）。internal 供單元測試。</summary>
    internal object BuildPayload(string audioB64, string targetText, bool structured = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = BasePrompt + "「" + (targetText ?? "").Trim() + "」" },
                        new { type = "input_audio", input_audio = new { data = audioB64, format = "wav" } },
                    },
                },
            },
        };
        if (structured)
        {
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "pronunciation_score",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            score = new { type = "integer", minimum = 0, maximum = 100 },
                            note = new { type = "string" },
                        },
                        required = new[] { "score", "note" },
                        additionalProperties = false,
                    },
                },
            };
        }
        return payload;
    }

    /// <summary>解析 OpenAI 回應為發音分數（internal 供單元測試）。分數鉗制於 0–100；缺欄/非 JSON 走 QueryException。</summary>
    internal static PronunciationResult Parse(string apiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(apiJson);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            if (message.TryGetProperty("refusal", out var refusal) && !string.IsNullOrWhiteSpace(refusal.GetString()))
            {
                throw new QueryException("Scoring request was refused.");
            }
            var content = MessageContentText(message);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new QueryException("Scoring response was empty.");
            }
            var extracted = ExtractJsonObject(content);
            if (!extracted.TrimStart().StartsWith("{"))
            {
                if (LooksLikeNoSpeech(content))
                {
                    return new PronunciationResult(0, "未偵測到朗讀");
                }
                throw new QueryException("Malformed scoring response: no JSON object.");
            }
            using var inner = JsonDocument.Parse(extracted);
            var r = inner.RootElement;
            if (!r.TryGetProperty("score", out var s))
            {
                throw new QueryException("Malformed scoring response: missing score.");
            }
            var score = s.ValueKind == JsonValueKind.Number ? s.GetInt32()
                : int.TryParse(s.GetString(), out var parsed) ? parsed : throw new QueryException("Score not a number.");
            score = Math.Clamp(score, 0, 100);
            var note = r.TryGetProperty("note", out var n) ? (n.GetString() ?? "") : "";
            return new PronunciationResult(score, note.Trim());
        }
        catch (QueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new QueryException("Failed to parse scoring response (malformed): " + ex.Message);
        }
    }

    /// <summary>
    /// 自模型回應取出 JSON 物件字串（internal 供單元測試）：音訊模型無 structured output，容忍 markdown
    /// 圍欄（```json … ```）與前後贅字——取第一個 <c>{</c> 至最後一個 <c>}</c>。
    /// </summary>
    internal static string ExtractJsonObject(string content)
    {
        var s = (content ?? "").Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) { s = s[(nl + 1)..]; }
            if (s.EndsWith("```")) { s = s[..^3]; }
            s = s.Trim();
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }

    private static string? MessageContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? "");
                }
            }
            return string.Join("\n", parts);
        }
        return content.GetRawText();
    }

    private static bool LooksLikeNoSpeech(string content)
    {
        var s = content ?? "";
        return s.Contains("未偵測", StringComparison.OrdinalIgnoreCase)
               || s.Contains("沒有偵測", StringComparison.OrdinalIgnoreCase)
               || s.Contains("沒有朗讀", StringComparison.OrdinalIgnoreCase)
               || s.Contains("no speech", StringComparison.OrdinalIgnoreCase)
               || s.Contains("no voice", StringComparison.OrdinalIgnoreCase)
               || s.Contains("no audio", StringComparison.OrdinalIgnoreCase)
               || s.Contains("silence", StringComparison.OrdinalIgnoreCase)
               || s.Contains("silent", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
