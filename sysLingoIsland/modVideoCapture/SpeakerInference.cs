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
