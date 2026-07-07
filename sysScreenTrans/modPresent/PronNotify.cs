namespace ScreenTrans.Present;

/// <summary>
/// 發音練習通知文案組裝（[modPresent模組] 發音回饋通知契約，spec#10／#101）：純函式、可單元測試——
/// **標題含目標英文句**（明載在練哪一句）、**內文含分數／門檻／過不過**＋可選 AI 建議，或各失敗態明訊。
/// </summary>
public static class PronNotify
{
    /// <summary>評分結果通知：內文＝「score / threshold ✓ passed」或「score / threshold — try again」＋可選建議（次行）。</summary>
    public static (string Title, string Body) Result(string target, int score, int threshold, string note)
    {
        var head = score >= threshold
            ? $"{score} / {threshold}  ✓ passed"
            : $"{score} / {threshold} — try again";
        var body = string.IsNullOrWhiteSpace(note) ? head : head + "\n" + note.Trim();
        return (Title(target), body);
    }

    /// <summary>失敗態通知：內文＝明確訊息（錄音太短／找不到麥克風／未授權／評分失敗／無網／未偵測到朗讀等）。</summary>
    public static (string Title, string Body) Failure(string target, string message) => (Title(target), FriendlyFailure(message));

    /// <summary>
    /// 將底層例外/API/HTTP/HRESULT 訊息收斂成使用者可理解的固定分類，避免通知露出 0x 代碼或原始錯誤細節。
    /// </summary>
    public static string FriendlyFailure(string? message)
    {
        var m = (message ?? "").Trim();
        if (m.Length == 0)
        {
            return "Scoring failed. Please try again.";
        }
        if (Has(m, "Recording too short")) return "Recording too short";
        if (Has(m, "No microphone")) return "No microphone found";
        if (Has(m, "Allow microphone")) return "Allow microphone access in Windows Privacy settings";
        if (Has(m, "No audio")) return "No audio was recorded. Hold the mic and speak clearly.";
        if (Has(m, "OPENAI_API_KEY") || Has(m, "OpenAI key")) return "Set your OpenAI key to score pronunciation";
        if (Has(m, "401") || Has(m, "403") || Has(m, "unauthorized") || Has(m, "forbidden") || Has(m, "invalid api key"))
        {
            return "OpenAI key was rejected. Check your API key.";
        }
        if (Has(m, "model") || Has(m, "unsupported") || Has(m, "audio") && Has(m, "400"))
        {
            return "Scoring model is not available. Check the pronunciation model setting.";
        }
        if (Has(m, "Network") || Has(m, "timed out") || Has(m, "timeout") || Has(m, "429") || Has(m, "transient") || Has(m, "503") || Has(m, "502") || Has(m, "500"))
        {
            return "Network or scoring service is busy. Please try again.";
        }
        if (Has(m, "parse") || Has(m, "malformed") || Has(m, "missing score") || Has(m, "empty"))
        {
            return "Scoring service returned an unreadable result. Please try again.";
        }
        if (Has(m, "0x") || Has(m, "HRESULT") || Has(m, "Exception"))
        {
            return "Scoring failed. Please try again.";
        }
        return m.StartsWith("Scoring failed", StringComparison.OrdinalIgnoreCase)
            ? "Scoring failed. Please try again."
            : m;
    }

    private static bool Has(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>通知標題：含目標句（空目標退回泛用標題）。</summary>
    private static string Title(string target)
        => string.IsNullOrWhiteSpace(target) ? "Pronunciation practice" : $"Pronunciation: {target.Trim()}";
}
