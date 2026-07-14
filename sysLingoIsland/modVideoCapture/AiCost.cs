namespace LingoIsland.Video;

/// <summary>
/// AI 呼叫費用估算（AI 動作對話視窗顯示「含 AI 費用」）：以模型別單價表估 USD，web_search 另計每次工具費。
/// <b>單價為訓練期公開參考價、可能已過時</b>——UI 一律標示為估算、請以 OpenAI 現行定價為準。純函式、可單元測試。
/// </summary>
public static class AiCost
{
    /// <summary>估算單價（USD / 1M tokens）：input／output。僅列常用模型；未列者無法估金額（UI 顯示 tokens 與 n/a）。</summary>
    private static readonly Dictionary<string, (double In, double Out)> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o-mini"] = (0.15, 0.60),
        ["gpt-4o"] = (2.50, 10.00),
        ["gpt-4.1-mini"] = (0.40, 1.60),
        ["gpt-4.1"] = (2.00, 8.00),
        ["gpt-4.1-nano"] = (0.10, 0.40),
    };

    /// <summary>web_search 工具每次呼叫之額外估計費（USD）；隨方案/模型而異，僅供概估。</summary>
    public const double WebSearchCallUsd = 0.01;

    /// <summary>是否有該模型之估價（供 UI 決定顯示金額或「n/a」）。</summary>
    public static bool HasRate(string? model) => Rates.ContainsKey((model ?? "").Trim());

    /// <summary>
    /// 估算某次呼叫費用（USD）：token 費（依模型單價）＋（<paramref name="webSearch"/> 時）一次 web_search 工具費。
    /// 模型未列於單價表 → 回 null（僅能顯示 tokens、金額標 n/a）。
    /// </summary>
    public static double? EstimateUsd(string? model, int inputTokens, int outputTokens, bool webSearch = false)
    {
        if (!Rates.TryGetValue((model ?? "").Trim(), out var r)) { return null; }
        var usd = inputTokens / 1_000_000.0 * r.In + outputTokens / 1_000_000.0 * r.Out;
        if (webSearch) { usd += WebSearchCallUsd; }
        return usd;
    }
}
