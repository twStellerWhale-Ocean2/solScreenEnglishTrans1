using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 「由逐字稿找影片」純邏輯（[modVideoCapture模組]，#189 獲得頁重構「搜尋區塊·由逐字稿」子頁）：組 <c>web_search</c> 提示、解析回應為候選影片清單。
/// 思路：先請模型【上網】找「該主題**有公開逐字稿可用**」的 YouTube 影片，回其標題／YouTube 連結／逐字稿來源；
/// UI 再據此定位實際影片（影片 ID 若模型未給或不可靠，改以 yt-dlp 依標題搜尋為準），故「**先找逐字稿、再找影片**」。
/// 純函式在此供單元測試；實網搜見 <see cref="OpenAiTranscriptVideoFinder"/>。
/// </summary>
public static class TranscriptVideoFind
{
    /// <summary>候選影片一筆：<see cref="Title"/> 影片標題、<see cref="VideoId"/>（模型給的 11 碼 ID，null＝UI 再以標題搜尋定位）、<see cref="Source"/> 逐字稿來源描述（如「PAW Patrol Wiki (Fandom)」）。</summary>
    public sealed record Candidate(string Title, string? VideoId, string Source);

    /// <summary>組「上網找有逐字稿可用之影片」提示（web_search）：回 <c>{videos:[{title, youtube_url, source}]}</c>，最多 <paramref name="max"/> 筆；找不到回空陣列。</summary>
    public static string BuildFindVideosPrompt(string topic, int max, string? videoTheme = null)
    {
        var sb = new StringBuilder();
        sb.Append("請【上網搜尋】找出最多 ").Append(max).Append(" 支**有公開逐字稿可用**的 YouTube 英語學習影片，主題為：").Append(topic.Trim()).Append('。');
        sb.Append("「有逐字稿可用」指網路上（官方腳本、熱門 fandom wiki、逐字稿網站等公評良好來源）找得到該影片**完整、且看得出說話者**之逐字稿。");
        sb.Append("若某來源系統性地為整個系列／頻道提供逐字稿（例如卡通的 fandom wiki、TED‑Ed／TED 的 ted.com、影集的逐字稿站），");
        sb.Append("請盡量從中列出**多支不同**且知名的真實影片、湊到接近 ").Append(max).Append(" 支——但每支都必須是**真實存在**、且該來源確實有其逐字稿者，寧缺勿杜撰。");
        if (!string.IsNullOrWhiteSpace(videoTheme)) { sb.Append("\n所屬主題／分類（縮小範圍）：").Append(videoTheme!.Trim()); }
        sb.Append("\n每支請給：title（影片標題）、youtube_url（該影片的 YouTube 連結或 11 碼影片 ID；不確定就填空字串，不要亂猜）、source（逐字稿來源網址或名稱）。");
        sb.Append("\n只回傳 JSON：{\"videos\":[{\"title\":\"…\",\"youtube_url\":\"…\",\"source\":\"…\"}]}。找不到就回 {\"videos\":[]}。不要輸出任何搜尋過程／思考／說明文字。");
        return sb.ToString();
    }

    private static readonly Regex IdInUrl = new(@"(?:v=|youtu\.be/|/embed/|/shorts/|/live/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex BareId = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    /// <summary>自 YouTube 連結或裸 11 碼 ID 取影片 ID；無法辨識回 null（比照 <c>VideoCapturePage.ExtractVideoId</c>，模組內自足不反依賴 UI）。</summary>
    internal static string? ExtractVideoId(string? input)
    {
        var s = (input ?? "").Trim();
        if (BareId.IsMatch(s)) { return s; }
        var m = IdInUrl.Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>解析 find 之 Responses 回應為候選清單（取 output_text 內之 JSON）；缺 videos／解析失敗回空。標題空白者略過；youtube_url 解不出 ID 者 <see cref="Candidate.VideoId"/>＝null（UI 再以標題定位）。</summary>
    public static IReadOnlyList<Candidate> ParseCandidates(string responsesApiJson)
    {
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = SpeakerInference.ExtractOutputText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(text)) { return Array.Empty<Candidate>(); }
        var s = text!;
        var start = s.IndexOf('{'); var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) { return Array.Empty<Candidate>(); }
        try
        {
            using var inner = JsonDocument.Parse(s.Substring(start, end - start + 1));
            var r = inner.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("videos", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Candidate>();
            }
            var list = new List<Candidate>();
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) { continue; }
                var title = Str(v, "title");
                if (string.IsNullOrWhiteSpace(title)) { continue; }
                list.Add(new Candidate(title!.Trim(), ExtractVideoId(Str(v, "youtube_url")), (Str(v, "source") ?? "").Trim()));
            }
            return list;
        }
        catch (JsonException) { return Array.Empty<Candidate>(); }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>合輯／長片彙編之標題關鍵字（多集混剪、無單一乾淨逐字稿，Script 對不上）。</summary>
    private static readonly string[] CompilationMarkers =
    {
        "& more", "and more", "compilation", "full episodes", "all episodes", "mega ", "marathon",
        "best of", "mashup", "non-stop", "nonstop", "hours of", "1 hour", "2 hour", "3 hour", "1hr", "2hr",
    };

    /// <summary>
    /// 標題是否像「合輯／長片彙編」（#189 實測修）：命中任一關鍵字即視為合輯。這類影片沒有單一乾淨逐字稿→
    /// 載入後 🌐 Script 找不到完整可對齊之逐字稿（正是實測「找到卻不能用」的主因），由 UI 於「由逐字稿找影片」定位時濾除。純函式、可單元測試。
    /// </summary>
    public static bool LooksLikeCompilation(string? title)
    {
        var t = (title ?? "").ToLowerInvariant();
        return CompilationMarkers.Any(m => t.Contains(m));
    }
}
