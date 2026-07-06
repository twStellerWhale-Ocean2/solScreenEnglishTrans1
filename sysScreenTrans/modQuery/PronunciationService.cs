using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScreenTrans.Query;

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
    public const string DefaultModel = "gpt-audio-mini";

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
        var json = await RunWithRetryAsync(c => SendOnceAsync(BuildPayload(b64, targetText), key, c), ct);
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
    /// 評分提示（spec#10）：**先判定音訊是否含對目標句之真正朗讀**——無朗讀（靜音／只有背景雜訊／與目標無關）
    /// 一律 score=0＋note「未偵測到朗讀」；有朗讀才評 0–100。區分「沒唸→0」與「唸得爛→低分（非 0）」以免偽陰性。
    /// internal 供單元測試。
    /// </summary>
    internal const string BasePrompt =
        "你是英語發音評分器。使用者應朗讀下列目標英文句，請評估其發音正確度。"
        + "請先判斷音訊中是否有『針對該目標句的真人朗讀』："
        + "若為靜音、只有背景雜訊、或與目標句無關的聲音（沒有真正朗讀該句），一律 score=0、note 註明「未偵測到朗讀」；"
        + "若確實有人朗讀該句，才依發音正確度給分——100＝接近母語者、80 左右＝大致正確可懂、40 以下＝明顯不準但仍聽得出在唸該句；"
        + "唸得不標準但確有朗讀者給對應低分（不可因發音差就當成沒朗讀、亦不可因背景雜訊而過度扣分）。"
        + "只輸出一個 JSON 物件、不要 markdown 圍欄、不要任何多餘文字，格式為 {\"score\": 整數0到100, \"note\": \"一句簡短繁體中文建議\"}。目標英文句：";

    /// <summary>組裝評分 payload（input_audio＋structured output）。internal 供單元測試。</summary>
    internal object BuildPayload(string audioB64, string targetText) => new
    {
        model = _model,
        messages = new object[]
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
    }; // 註：gpt-audio-* 音訊模型不支援 response_format(json_schema/json_object)，改以提示要求 JSON、Parse 穩健解析

    /// <summary>解析 OpenAI 回應為發音分數（internal 供單元測試）。分數鉗制於 0–100；缺欄/非 JSON 走 QueryException。</summary>
    internal static PronunciationResult Parse(string apiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(apiJson);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new QueryException("Scoring response was empty.");
            }
            using var inner = JsonDocument.Parse(ExtractJsonObject(content));
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

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
