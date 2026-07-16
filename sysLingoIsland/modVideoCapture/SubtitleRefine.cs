using System.Text;
using System.Text.Json;

namespace LingoIsland.Video;

/// <summary>一段重分句結果（#189 Row2）：起於原字幕第 <see cref="StartIndex"/> 格（其時間即本段時間）、整句 <see cref="Text"/>、說話人 <see cref="Speaker"/>（null＝未知）。</summary>
public sealed record RefinedSegment(int StartIndex, string Text, string? Speaker);

/// <summary>
/// 字幕重分句之純函式（[modVideoCapture模組]，#189 Row2「AI 分析」）：把（尤指 Auto 基底）破碎/斷句錯的逐句字幕
/// 交 LLM 重新併為完整句＋標說話人，**時間不變**——每個新段沿用其起始原格的時間（<see cref="BuildCues"/>），故不動時間軸、只修斷句與講話人。
/// 不依賴網路／UI，可單元測試；HTTP 由 <see cref="OpenAiRefiner"/> 負責。
/// </summary>
public static class SubtitleRefine
{
    /// <summary>組重分句提示（逐句編號、要求回 <c>{"segments":[{startIndex,text,speaker}]}</c>，startIndex 遞增、只用既有文字不新增內容）。</summary>
    public static string BuildPrompt(IReadOnlyList<SubtitleCue> cues, string? videoTitle, string? videoTheme = null)
    {
        var sb = new StringBuilder();
        sb.Append("下面是一支影片的英文字幕逐句（已編號 0..").Append(cues.Count - 1).Append("）。這些字幕的斷句常是錯的（一句被切成好幾格、或好幾句黏成一格）。");
        sb.Append("請把它們**重新併成自然、完整的句子**（供英語學習者閱讀），規則：");
        sb.Append("\n- 合併屬於同一句的連續格；維持時間先後順序。");
        sb.Append("\n- **只用既有文字**，可修正明顯的空白／大小寫／標點，但**不得新增或竄改內容、不得翻譯**。");
        sb.Append("\n- 每個併好的句子輸出：startIndex＝該句**開頭**所在的原格編號（整份必須嚴格遞增）、text＝完整句子、speaker＝最可能的**具體**說話人名（無法判斷或音效/音樂類回 \"unknown\"）。");
        if (!string.IsNullOrWhiteSpace(videoTitle)) { sb.Append("\n影片標題（輔助判斷角色）：").Append(videoTitle.Trim()); }
        if (!string.IsNullOrWhiteSpace(videoTheme)) { sb.Append("\n所屬主題／分類：").Append(videoTheme.Trim()); }
        sb.Append("\n只回傳 JSON：{\"segments\":[{\"startIndex\":int,\"text\":str,\"speaker\":str}, ...]}。不要輸出任何說明或思考文字。\n\n逐句：");
        for (var i = 0; i < cues.Count; i++)
        {
            sb.Append('\n').Append(i).Append(": ").Append(cues[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>解析 OpenAI chat 回應（<c>choices[0].message.content</c> 為 <c>{"segments":[...]}</c>）為段序列；空／缺 segments 回空清單。malformed envelope 擲例外（呼叫端轉可讀失敗）。</summary>
    public static IReadOnlyList<RefinedSegment> ParseSegments(string apiJson)
    {
        using var doc = JsonDocument.Parse(apiJson);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return ParseSegmentsContent(content);
    }

    /// <summary>自模型輸出文字抽 <c>{"segments":[...]}</c>（容忍 ``` 圍籬與前後贅字）；解析失敗回空。internal 供單元測試。</summary>
    internal static IReadOnlyList<RefinedSegment> ParseSegmentsContent(string? content)
    {
        var list = new List<RefinedSegment>();
        if (string.IsNullOrWhiteSpace(content)) { return list; }
        var s = content!;
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var nl = s.IndexOf('\n', fence);
            var close = nl >= 0 ? s.IndexOf("```", nl + 1, StringComparison.Ordinal) : -1;
            if (nl >= 0 && close > nl) { s = s.Substring(nl + 1, close - nl - 1); }
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) { return list; }
        try
        {
            using var inner = JsonDocument.Parse(s.Substring(start, end - start + 1));
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("segments", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }
            foreach (var seg in arr.EnumerateArray())
            {
                var idx = seg.TryGetProperty("startIndex", out var si) && si.ValueKind == JsonValueKind.Number && si.TryGetInt32(out var iv) ? iv : -1;
                var text = seg.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
                var sp = seg.TryGetProperty("speaker", out var spp) && spp.ValueKind == JsonValueKind.String ? spp.GetString() : null;
                list.Add(new RefinedSegment(idx, text, sp));
            }
        }
        catch (JsonException) { return new List<RefinedSegment>(); }
        return list;
    }

    /// <summary>
    /// 純函式：以重分句段序列＋原字幕，建出新逐句 cue——每段時間＝其 <see cref="RefinedSegment.StartIndex"/> 原格之時間（**時間不變**）。
    /// 界外 index／空白文字略過；speaker 經 <see cref="SpeakerInference.CleanSpeaker"/> 清理（unknown／音效等→null）；依時間穩定排序。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> BuildCues(IReadOnlyList<SubtitleCue> baseCues, IReadOnlyList<RefinedSegment> segments)
    {
        var result = new List<SubtitleCue>();
        foreach (var s in segments)
        {
            if (s.StartIndex < 0 || s.StartIndex >= baseCues.Count) { continue; }
            var text = (s.Text ?? "").Trim();
            if (text.Length == 0) { continue; }
            var speaker = SpeakerInference.CleanSpeaker(s.Speaker);
            result.Add(new SubtitleCue(text, baseCues[s.StartIndex].StartSec, speaker));
        }
        return result.OrderBy(c => c.StartSec).ToList();
    }
}
