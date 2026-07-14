using System.Text;
using System.Text.Json;

namespace LingoIsland.Video;

/// <summary>
/// 說話人推斷之純函式（[modVideoCapture模組]，epic #145 增量6，#156）：組推斷提示、解析 OpenAI 回應為
/// 逐句說話人、非破壞疊加回 cue。不依賴網路／UI，可單元測試；HTTP 由 <see cref="OpenAiSpeakerEnricher"/> 負責。
/// **推斷來源為台詞文字＋常識、非觀看畫面**——標註為推斷、非 ground truth。
/// </summary>
public static class SpeakerInference
{
    /// <summary>組逐句編號之推斷提示（含可選影片標題輔助判斷角色）；要求回 <c>{"speakers":[...]}</c> 依序對應。</summary>
    public static string BuildPrompt(IReadOnlyList<SubtitleCue> cues, string? videoTitle)
    {
        var sb = new StringBuilder();
        sb.Append("下面是一支影片的英文字幕逐句（已編號）。請依對話內容與常識，推斷每一句最可能的說話者名稱");
        sb.Append("（角色名或人名）。這是**根據台詞文字的推斷、非觀看畫面**；無法判斷者回空字串。");
        if (!string.IsNullOrWhiteSpace(videoTitle))
        {
            sb.Append("\n影片標題（輔助判斷角色，僅供參考）：").Append(videoTitle.Trim());
        }
        sb.Append("\n回傳 JSON：{\"speakers\":[...]}，speakers 為字串陣列、長度與句數相同、依序一一對應（第 n 句對第 n 個）。\n\n逐句：");
        for (var i = 0; i < cues.Count; i++)
        {
            sb.Append('\n').Append(i + 1).Append(". ").Append(cues[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析 OpenAI 回應（<c>choices[0].message.content</c> 為 <c>{"speakers":[...]}</c>）為逐句說話人清單：
    /// 空字串／非字串→null（未知）。缺 speakers 或空內容回空清單。malformed JSON 擲例外（由呼叫端轉可讀失敗）。
    /// </summary>
    public static IReadOnlyList<string?> ParseSpeakers(string apiJson)
    {
        using var doc = JsonDocument.Parse(apiJson);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<string?>();

        using var inner = JsonDocument.Parse(content);
        if (inner.RootElement.ValueKind != JsonValueKind.Object
            || !inner.RootElement.TryGetProperty("speakers", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string?>();
        }
        var list = new List<string?>();
        foreach (var e in arr.EnumerateArray())
        {
            var s = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            list.Add(string.IsNullOrWhiteSpace(s) ? null : s!.Trim());
        }
        return list;
    }

    // ---- 網路搜尋來源（epic #145 增量6b，#145 §D 第二來源）：OpenAI Responses API＋web_search 工具 ----

    /// <summary>組【上網搜尋】提示：請模型搜該影集逐字稿/角色資料（優先熱門可信來源）判斷每句說話者，只回 <c>{"speakers":[...]}</c>。</summary>
    public static string BuildWebPrompt(IReadOnlyList<SubtitleCue> cues, string? videoTitle)
    {
        var sb = new StringBuilder();
        sb.Append("下面是一支影片的英文字幕逐句（已編號）。請【上網搜尋】這支影片／影集的逐字稿或角色台詞資料");
        sb.Append("（優先採用熱門、可信來源，如官方或 fandom wiki 的逐字稿），據以判斷每一句最可能的說話者名稱（角色名）。");
        sb.Append("無法確定者回空字串。");
        if (!string.IsNullOrWhiteSpace(videoTitle))
        {
            sb.Append("\n影片標題（搜尋與判斷角色用）：").Append(videoTitle.Trim());
        }
        sb.Append("\n**只**回傳 JSON 物件、不要任何說明文字或 markdown 圍籬：{\"speakers\":[...]}，");
        sb.Append("speakers 為字串陣列、長度與句數完全相同、依序一一對應（第 n 句對第 n 個）。\n\n逐句：");
        for (var i = 0; i < cues.Count; i++)
        {
            sb.Append('\n').Append(i + 1).Append(". ").Append(cues[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析 OpenAI Responses API 回應為逐句說話人：自 <c>output[]</c> 中 <c>type=="message"</c> 之
    /// <c>content[] output_text.text</c> 取模型文字，再抽出其中的 <c>{"speakers":[...]}</c>（容忍圍籬與前後贅字）。
    /// 空／無 speakers 回空清單；envelope 為 malformed JSON 擲例外（由呼叫端轉可讀失敗）。
    /// </summary>
    public static IReadOnlyList<string?> ParseWebSpeakers(string responsesApiJson)
    {
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = ExtractOutputText(doc.RootElement);
        return string.IsNullOrWhiteSpace(text) ? Array.Empty<string?>() : ParseSpeakersJson(text!);
    }

    /// <summary>自 Responses 回應取模型輸出文字：優先便捷欄 <c>output_text</c>，否則彙整 <c>output[] message → content[] output_text.text</c>。無則 null。</summary>
    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
        {
            return ot.GetString();
        }
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var t) && t.GetString() == "message"
                && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in content.EnumerateArray())
                {
                    if (c.TryGetProperty("type", out var ct) && ct.GetString() == "output_text"
                        && c.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(txt.GetString());
                    }
                }
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>自模型輸出文字抽 <c>{"speakers":[...]}</c>：去 ``` 圍籬、取首個 '{' 到末個 '}'；非物件/無 speakers/解析失敗回空。空字串→null。</summary>
    private static IReadOnlyList<string?> ParseSpeakersJson(string content)
    {
        var s = content;
        var fence = s.IndexOf("```", StringComparison.Ordinal); // 去 ```json … ``` 圍籬
        if (fence >= 0)
        {
            var nl = s.IndexOf('\n', fence);
            var close = nl >= 0 ? s.IndexOf("```", nl + 1, StringComparison.Ordinal) : -1;
            if (nl >= 0 && close > nl) { s = s.Substring(nl + 1, close - nl - 1); }
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) { return Array.Empty<string?>(); }
        try
        {
            using var inner = JsonDocument.Parse(s.Substring(start, end - start + 1));
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("speakers", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string?>();
            }
            var list = new List<string?>();
            foreach (var e in arr.EnumerateArray())
            {
                var v = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                list.Add(string.IsNullOrWhiteSpace(v) ? null : v!.Trim());
            }
            return list;
        }
        catch (JsonException) { return Array.Empty<string?>(); }
    }

    // ---- API 用量（供費用估算，AI 動作對話視窗）：chat（prompt/completion）與 Responses（input/output）皆掛 root.usage ----

    /// <summary>自 OpenAI 回應取 token 用量（chat 的 prompt/completion_tokens 或 Responses 的 input/output_tokens，皆在 root.usage）；缺 usage／解析失敗回 null。</summary>
    public static SpeakerUsage? ParseUsage(string apiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(apiJson);
            if (!doc.RootElement.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) { return null; }
            var inTok = GetInt(u, "prompt_tokens", "input_tokens");
            var outTok = GetInt(u, "completion_tokens", "output_tokens");
            var total = GetInt(u, "total_tokens") ?? ((inTok ?? 0) + (outTok ?? 0));
            if (inTok is null && outTok is null && total == 0) { return null; }
            return new SpeakerUsage(inTok ?? 0, outTok ?? 0, total);
        }
        catch { return null; }
    }

    private static int? GetInt(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) { return i; }
        }
        return null;
    }

    /// <summary>
    /// 非破壞疊加：把逐句推斷之 <paramref name="speakers"/> 併回 <paramref name="cues"/>——僅填補**未標示**說話人之句，
    /// 既有具名說話人（如 VTT ground truth）一律保留。長度不符時以較短者為界、其餘 cue 原樣。文字/時間不動。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> MergeSpeakers(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<string?> speakers)
    {
        var result = new List<SubtitleCue>(cues.Count);
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var inferred = i < speakers.Count ? speakers[i] : null;
            if (string.IsNullOrEmpty(cue.Speaker) && !string.IsNullOrWhiteSpace(inferred))
            {
                result.Add(cue with { Speaker = inferred!.Trim() });
            }
            else
            {
                result.Add(cue);
            }
        }
        return result;
    }

    /// <summary>疊加後實際新增說話人標註（原未標示→新有值）之句數，供狀態訊息。</summary>
    public static int CountNewlyLabeled(IReadOnlyList<SubtitleCue> before, IReadOnlyList<SubtitleCue> after)
    {
        var n = 0;
        var count = Math.Min(before.Count, after.Count);
        for (var i = 0; i < count; i++)
        {
            if (string.IsNullOrEmpty(before[i].Speaker) && !string.IsNullOrEmpty(after[i].Speaker)) n++;
        }
        return n;
    }
}
