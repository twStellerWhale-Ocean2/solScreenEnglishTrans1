using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 「由字幕檔網址配影片」純邏輯（[modVideoCapture模組]，epic #178 增量2〔由逐字稿改造〕，#182）：組 <c>web_search</c> 提示、
/// 解析回應為候選影片。思路自增量1 之「先給主題→AI 猜有逐字稿之影片」改為「**使用者貼字幕檔網址→AI 解析驗證→配 YouTube**」。
/// 真 API smoke 發現單次呼叫對「目錄頁」只讀目錄本身、不鑽進各劇本連結（回 0 支），USR 拍板改**兩階段**：
/// <b>第1階段（列連結）</b> <see cref="BuildListLinksPrompt"/>／<see cref="ParseTranscriptLinks"/>——打開輸入網址，單檔頁回其自身一筆、目錄頁列各字幕檔連結（去重濾失效，最多 max）；
/// <b>第2階段（逐支驗證＋配片）</b> <see cref="BuildValidateOnePrompt"/>／<see cref="ParseOneCandidate"/>——逐份打開、**驗證含說話人**（時間可有可無）、合格者配對 YouTube，回其標題／連結／來源／**字幕檔原始 URL**。
/// UI 再以 yt-dlp 依標題定位實際影片、濾合輯、篩可載入。純函式在此供單元測試（列連結解析、單支驗證、含說話人啟發式、連結去重、schema 形狀）；實網搜見 <see cref="OpenAiTranscriptVideoFinder"/>。
/// </summary>
public static class TranscriptVideoFind
{
    /// <summary>
    /// 候選影片一筆：<see cref="Title"/> 影片標題、<see cref="VideoId"/>（模型給的 11 碼 ID，null＝UI 再以標題搜尋定位）、
    /// <see cref="Source"/> 字幕檔**來源描述**字串（如「PAW Patrol Wiki (Fandom)」，語意＝人看的來源名）、
    /// <see cref="TranscriptUrl"/> 字幕檔**原始 URL**（#182，語意＝可導覽之連結，與 <see cref="Source"/> 描述分清——供增量3 表格 Web 欄超連結、增量5 主從合成之逐字稿來源）。
    /// </summary>
    public sealed record Candidate(string Title, string? VideoId, string Source, string? TranscriptUrl = null);

    /// <summary>
    /// 第1階段——組「列出字幕檔連結」提示（web_search，#182 兩階段）：請模型上網打開 <paramref name="subtitleUrl"/>，判斷是
    /// (一)【單一字幕檔頁】→回其自身一筆 <c>{title, transcript_url=輸入網址}</c>；或 (二)【目錄頁】→列出其中各字幕檔連結
    /// （最多 <paramref name="max"/> 份、去重、濾失效）各一筆 <c>{title, transcript_url}</c>。此步**只列連結、先不逐一開啟判斷說話人**（第2階段再做）。
    /// 回 <c>{links:[{title, transcript_url}]}</c>；打不開回空陣列。
    /// </summary>
    public static string BuildListLinksPrompt(string subtitleUrl, int max)
    {
        var sb = new StringBuilder();
        sb.Append("請【上網打開並閱讀】這個網址：").Append(subtitleUrl.Trim()).Append('。');
        sb.Append("判斷它是 (一)【單一字幕檔／逐字稿頁】——某一支影片的完整逐字稿；或 (二)【目錄頁】——一頁列出多份字幕檔／逐字稿的連結。");
        sb.Append("若是 (一) 單一字幕檔頁，只回一筆：title（該影片／劇本標題）、transcript_url（**就填這個輸入網址本身**）。");
        sb.Append("若是 (二) 目錄頁，請列出其中各字幕檔／逐字稿連結（最多 ").Append(max).Append(" 份；**去除重複與打不開／失效的連結**），每份一筆：title（該份標題）、transcript_url（**該字幕檔本身的原始網址**，須為絕對 http/https 連結）。");
        sb.Append("此步驟**只需列出連結、先不要開啟各連結內容去判斷說話人**（下一步會逐一開啟驗證）。寧缺勿杜撰：連結不確定或打不開就不要納入。");
        sb.Append("\n只回傳 JSON：{\"links\":[{\"title\":\"…\",\"transcript_url\":\"…\"}]}。");
        sb.Append("網址打不開時回 {\"links\":[]}。不要輸出任何搜尋過程／思考／說明文字。");
        return sb.ToString();
    }

    /// <summary>
    /// 第2階段——組「驗證單份字幕檔含說話人＋配 YouTube」提示（web_search，#182 兩階段）：請模型打開 <paramref name="transcriptUrl"/>
    /// （可選 <paramref name="title"/> 輔助辨識），先確認是否**看得出說話人（角色名）**——每句『角色：台詞』或 VTT 語音標記 <c>&lt;v 名字&gt;</c>，**時間軸可有可無**；
    /// 合格者再找出對應之 YouTube 影片。回單筆 <c>{title, youtube_url, source, transcript_url, has_speaker, sample}</c>；看不出說話人／打不開時 <c>has_speaker=false</c>。
    /// 可選 <paramref name="videoTheme"/>（FindAsync 帶入之所屬主題）供配對參考。
    /// </summary>
    public static string BuildValidateOnePrompt(string transcriptUrl, string? title = null, string? videoTheme = null)
    {
        var sb = new StringBuilder();
        sb.Append("請【上網打開並閱讀】這一份字幕檔／逐字稿：").Append(transcriptUrl.Trim()).Append('。');
        if (!string.IsNullOrWhiteSpace(title)) { sb.Append("（其標題約為：").Append(title!.Trim()).Append("）"); }
        sb.Append("第一步先確認這份字幕檔是否**看得出說話人（角色名）**——例如每句『角色：台詞』，或字幕語音標記 <v 名字>；**時間軸可有可無**。");
        sb.Append("若看不出說話人，回 has_speaker=false、其餘欄位留空字串即可，不必勉強配片。");
        sb.Append("若看得出說話人（合格），請找出它對應的那一支 YouTube 影片，回其標題與（若確定）YouTube 連結。寧缺勿杜撰：不確定就留空、不要亂猜。");
        if (!string.IsNullOrWhiteSpace(videoTheme)) { sb.Append("\n這支影片所屬主題／分類（供配對參考）：").Append(videoTheme!.Trim()); }
        sb.Append("\n請回傳單筆物件：title（該影片標題，供之後定位）、youtube_url（該影片的 YouTube 連結或 11 碼影片 ID；不確定就填空字串）、");
        sb.Append("source（字幕檔來源網站名稱，如「PAW Patrol Wiki (Fandom)」）、transcript_url（**這份字幕檔的原始網址**，就填上面這個輸入網址）、");
        sb.Append("has_speaker（true/false，這份字幕檔是否看得出說話人）、sample（該字幕檔開頭數行原文，需含『角色：台詞』數行以便核對；沒有就填空字串）。");
        sb.Append("\n只回傳 JSON：{\"title\":\"…\",\"youtube_url\":\"…\",\"source\":\"…\",\"transcript_url\":\"…\",\"has_speaker\":true,\"sample\":\"…\"}。");
        sb.Append("打不開或看不出說話人時，has_speaker 填 false。不要輸出任何搜尋過程／思考／說明文字。");
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

    // ── 純函式：驗證單檔含說話人（#182「純文字啟發式」；模型另回 has_speaker 判定，兩者擇一為據，見 SpeakerQualifies） ──

    private static readonly Regex VttVoiceTag = new(@"<v[ .][^>]+>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // 「角色：台詞」行：以字母開頭、短標籤（≤~24 字、允許空格/點/連字號/&）、冒號（半形或全形）後緊接非空白且非 '/' 之台詞
    // （排除 http(s):// 之「協定：//…」與 00:12 之數字時間戳——標籤須字母開頭；台詞首字非 '/'）。
    private static readonly Regex SpeakerLine = new(@"^[\p{L}][\p{L}.'&\- ]{0,22}[\p{L}.)]\s*[:：]\s*[^\s/]", RegexOptions.Compiled);

    /// <summary>
    /// 純文字啟發式：<paramref name="transcript"/> 是否**看得出說話人**（#182）——命中 VTT 語音標記 <c>&lt;v 名字&gt;</c>（≥1），
    /// 或有 ≥2 行「角色：台詞」樣式即算含說話人（時間可有可無）。空白／無此樣式回 false。刻意排除 <c>http(s)://</c> 與數字時間戳之偽命中。
    /// </summary>
    public static bool HasSpeakerMarkup(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) { return false; }
        var t = transcript!;
        if (VttVoiceTag.IsMatch(t)) { return true; }
        var hits = 0;
        foreach (var raw in t.Split('\n'))
        {
            if (SpeakerLine.IsMatch(raw.Trim()) && ++hits >= 2) { return true; }
        }
        return false;
    }

    // ── 純函式：連結去重＋濾失效（#182：目錄頁多連結） ──

    /// <summary>字幕檔連結是否可用（#182「濾失效」之純層次）：非空、為絕對 http/https URL。真正的死鏈探測需網路（AI web_search 已代訪），此處只濾空白／畸形。</summary>
    public static bool IsUsableTranscriptUrl(string? url) => NormalizeTranscriptUrl(url) is not null;

    /// <summary>
    /// 正規化字幕檔連結為**去重鍵**（#182）：非絕對 http/https 回 null；否則 host 轉小寫並去 <c>www.</c>、去尾斜線、丟棄 fragment、保留 path/query。
    /// 供 <see cref="DedupLinks"/> 與 <see cref="ParseTranscriptLinks"/> 判定重複；非顯示用。
    /// </summary>
    public static string? NormalizeTranscriptUrl(string? url)
    {
        var s = url?.Trim();
        if (string.IsNullOrEmpty(s)) { return null; }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) { return null; }
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) { return null; }
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) { host = host.Substring(4); }
        var path = u.AbsolutePath.TrimEnd('/');
        return host + path + u.Query; // 丟棄 fragment（#…）；path/query 大小寫保留（部分站台路徑大小寫敏感）
    }

    /// <summary>連結去重＋濾失效（#182 純函式）：濾掉空白／畸形連結、以 <see cref="NormalizeTranscriptUrl"/> 為鍵去重（保留首見之原字串、次序穩定）。</summary>
    public static IReadOnlyList<string> DedupLinks(IEnumerable<string?> urls)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in urls)
        {
            var key = NormalizeTranscriptUrl(raw);
            if (key is null || !seen.Add(key)) { continue; }
            result.Add(raw!.Trim());
        }
        return result;
    }

    /// <summary>
    /// 第1階段——解析「列連結」之 Responses 回應為 <c>(Title, TranscriptUrl)</c> 清單（#182 兩階段；取 output_text 內之 JSON、容忍圍籬贅字）；缺 links／解析失敗回空。
    /// 逐筆：標題空白略過；字幕檔 URL 以 <see cref="IsUsableTranscriptUrl"/> 濾失效（畸形／空白）——**單一字幕檔頁**（僅 1 筆）模型省略 transcript_url 時退用 <paramref name="inputUrl"/>；
    /// 以 <see cref="NormalizeTranscriptUrl"/> 為鍵**去重**（保留首見原字串、次序穩定）。回傳供第2階段逐支驗證＋配片。
    /// </summary>
    public static IReadOnlyList<(string Title, string TranscriptUrl)> ParseTranscriptLinks(string responsesApiJson, string? inputUrl = null)
    {
        var empty = Array.Empty<(string, string)>();
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = SpeakerInference.ExtractOutputText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(text)) { return empty; }
        var s = text!;
        var start = s.IndexOf('{'); var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) { return empty; }
        try
        {
            using var inner = JsonDocument.Parse(s.Substring(start, end - start + 1));
            var r = inner.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("links", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return empty;
            }
            var entries = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).ToList();
            var single = entries.Count == 1; // 單一字幕檔頁：模型省略 transcript_url 時退用輸入網址本身
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<(string, string)>();
            foreach (var v in entries)
            {
                var title = Str(v, "title");
                if (string.IsNullOrWhiteSpace(title)) { continue; }
                var url = Str(v, "transcript_url");
                if (NormalizeTranscriptUrl(url) is null && single) { url = inputUrl; } // 單檔頁退用輸入網址
                var key = NormalizeTranscriptUrl(url);
                if (key is null) { continue; }          // 濾失效：無可用字幕檔連結
                if (!seen.Add(key)) { continue; }       // 連結去重
                list.Add((title!.Trim(), url!.Trim()));
            }
            return list;
        }
        catch (JsonException) { return empty; }
    }

    /// <summary>
    /// 第2階段——解析「驗證單份」之 Responses 回應為單筆 <see cref="Candidate"/>（#182 兩階段；取 output_text 內之單一物件、容忍圍籬贅字）；
    /// **驗證含說話人**（有 sample 以 <see cref="HasSpeakerMarkup"/> 覆核可抓模型幻覺、否則採模型 has_speaker）不合格或無標題／解析失敗回 <c>null</c>。
    /// 字幕檔 URL 一律採第2階段已知之 <paramref name="transcriptUrl"/>（第1階段驗過、比模型自報可靠）；youtube_url 解不出 ID 者 <see cref="Candidate.VideoId"/>＝null（UI 再以標題定位）。
    /// </summary>
    public static Candidate? ParseOneCandidate(string responsesApiJson, string transcriptUrl)
    {
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = SpeakerInference.ExtractOutputText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var s = text!;
        var start = s.IndexOf('{'); var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) { return null; }
        try
        {
            using var inner = JsonDocument.Parse(s.Substring(start, end - start + 1));
            var v = inner.RootElement;
            if (v.ValueKind != JsonValueKind.Object) { return null; }
            var title = Str(v, "title");
            if (string.IsNullOrWhiteSpace(title)) { return null; }  // 無標題無從定位配片
            if (!SpeakerQualifies(v)) { return null; }             // 驗證含說話人（啟發式優先、否則模型判定）
            return new Candidate(title!.Trim(), ExtractVideoId(Str(v, "youtube_url")), (Str(v, "source") ?? "").Trim(), transcriptUrl.Trim());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>該筆字幕檔是否含說話人（#182）：有 sample 原文→以純文字啟發式 <see cref="HasSpeakerMarkup"/> 核對（可抓模型 has_speaker 幻覺）；無 sample→採模型 has_speaker 判定。</summary>
    private static bool SpeakerQualifies(JsonElement v)
    {
        var sample = Str(v, "sample");
        return !string.IsNullOrWhiteSpace(sample) ? HasSpeakerMarkup(sample) : Bool(v, "has_speaker") == true;
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>合輯／長片彙編之標題關鍵字（多集混剪、無單一乾淨逐字稿，Script 對不上）。</summary>
    private static readonly string[] CompilationMarkers =
    {
        "& more", "and more", "compilation", "full episodes", "all episodes", "mega ", "marathon",
        "best of", "mashup", "non-stop", "nonstop", "hours of", "1 hour", "2 hour", "3 hour", "1hr", "2hr",
    };

    /// <summary>
    /// 標題是否像「合輯／長片彙編」（#189 實測修）：命中任一關鍵字即視為合輯。這類影片沒有單一乾淨逐字稿→
    /// 載入後找不到完整可對齊之逐字稿（正是實測「找到卻不能用」的主因），由 UI 於配對定位時濾除。純函式、可單元測試。
    /// </summary>
    public static bool LooksLikeCompilation(string? title)
    {
        var t = (title ?? "").ToLowerInvariant();
        return CompilationMarkers.Any(m => t.Contains(m));
    }
}
