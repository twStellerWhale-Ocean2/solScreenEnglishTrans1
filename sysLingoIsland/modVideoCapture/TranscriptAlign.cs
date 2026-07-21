using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 字幕主線之純函式（[modVideoCapture模組]，epic #178 增量6′-B「時間 pivot」定案）：把抓回之字幕/逐字稿網頁化為純文字
/// （<see cref="StripToPlainText"/>）、組「逐句抽取字幕（時間＋說話人＋台詞）」提示（<see cref="BuildExtractPrompt"/>）、
/// 解析抽取回應為逐句 <see cref="SubtitleCue"/>（<see cref="ParseExtractedCues"/>、時間以 <see cref="ParseFlexibleTime"/> 照抄）。
/// 不依賴網路／UI，可單元測試；HTTP 由 <see cref="OpenAiTranscriptAligner"/> 負責。回應解析沿用 <see cref="SpeakerInference.ExtractOutputText"/>（Responses 信封取模型文字）。
/// </summary>
public static class TranscriptAlign
{
    private static readonly Regex ScriptStyle = new(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BlockTag = new(@"</?(p|div|br|li|tr|h[1-6]|ul|ol|table|blockquote|section|article)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpacesTabs = new(@"[ \t\f\v]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// 把抓回之字幕檔／逐字稿頁（HTML 或純文字）化為**純文字**供 AI 解析（純函式）：去 <c>script/style</c> 區塊、
    /// 區塊級標籤（<c>p/div/br/li/tr/h1-6…</c>）轉換行以保留逐句結構、去其餘標籤、解 HTML 實體、收合行內空白與過多空行。
    /// null／空回空字串。<b>不判斷對白</b>（雜訊留待 AI 解析階段濾除）。
    /// </summary>
    public static string StripToPlainText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return ""; }
        var s = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        s = ScriptStyle.Replace(s, "\n");         // 去 script/style 全區塊（含內容）
        s = BlockTag.Replace(s, "\n");            // 區塊級標籤→換行（保留逐句/逐段界線）
        s = AnyTag.Replace(s, "");                // 去其餘行內標籤
        s = WebUtility.HtmlDecode(s);             // 解 &amp; &lt; &#39; &nbsp; … 等實體
        // 逐行收合行內空白、去空行；再限制連續空行至多兩行。
        var lines = s.Split('\n').Select(l => SpacesTabs.Replace(l, " ").Trim());
        s = string.Join("\n", lines);
        s = BlankLines.Replace(s, "\n\n");
        return s.Trim();
    }

    // ── 直接抽取（增量6′-B「時間 pivot」定案）：AI 讀網頁純文字、逐句抽「時間戳＋說話人＋台詞」（時間照抄、不估算）──

    /// <summary>組「逐句抽取字幕（時間＋說話人＋台詞）」提示（不上網，增量6′-B）：時間戳一律**照網頁原樣抄出、不推算/不編造**；回 <c>{cues:[{time,speaker,text}]}</c>。</summary>
    public static string BuildExtractPrompt(string transcriptText)
    {
        var sb = new StringBuilder();
        sb.Append("下面是一支英文影片的字幕／逐字稿**網頁純文字**（版面五花八門,可能夾雜導覽、廣告、頁尾、集數資訊等雜訊）。請**逐句抽取字幕**。\n");
        sb.Append("每句輸出三欄：time＝該句在網頁上標示的**時間戳,原樣照抄**（如「00:00:47」「1:22」；該句若無時間戳就給空字串）；speaker＝說話者角色名（原文標明「角色：台詞」則取,無則空字串）；text＝台詞原文。\n");
        sb.Append("鐵則：時間戳與台詞**一律照網頁原樣抄出——不要自行推算、估算、換算、調整或編造任何時間**；保持出現順序、不要翻譯改寫；略過導覽/廣告/頁尾/章節標題/集數資訊等非字幕內容。\n");
        sb.Append("只回傳 JSON：{\"cues\":[{\"time\":\"時間戳或空字串\",\"speaker\":\"角色名或空字串\",\"text\":\"台詞\"}, ...]}。不要輸出任何說明文字。\n\n");
        sb.Append("網頁內容：\n---\n").Append(transcriptText.Trim()).Append("\n---");
        return sb.ToString();
    }

    /// <summary>解析「直接抽取」之 Responses 回應為逐句 <see cref="SubtitleCue"/>（取 output_text 內之 <c>{cues:[{time,speaker,text}]}</c>；容忍圍籬）：時間以 <see cref="ParseFlexibleTime"/> 解析（解不出＝null）；台詞空白之句略過；說話人空白＝未標示（null）。純函式。</summary>
    public static IReadOnlyList<SubtitleCue> ParseExtractedCues(string responsesApiJson)
    {
        var cues = new List<SubtitleCue>();
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = SpeakerInference.ExtractOutputText(doc.RootElement);
        var json = ExtractJsonObject(text);
        if (json is null) { return cues; }
        try
        {
            using var inner = JsonDocument.Parse(json);
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("cues", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return cues;
            }
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) { continue; }
                var lineText = (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)?.Trim();
                if (string.IsNullOrEmpty(lineText)) { continue; }
                var timeStr = el.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.String ? tm.GetString() : null;
                var speaker = (el.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null)?.Trim();
                cues.Add(new SubtitleCue(lineText, ParseFlexibleTime(timeStr), string.IsNullOrEmpty(speaker) ? null : speaker));
            }
        }
        catch (JsonException) { /* malformed → 回已解析部分 */ }
        return cues;
    }

    private static readonly Regex ClockRe = new(@"(?<h>\d{1,2}):(?<m>\d{1,2}):(?<s>\d{1,2}(?:\.\d+)?)|(?<m2>\d{1,2}):(?<s2>\d{1,2}(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>彈性時間戳解析（純函式，增量6′-B）：<c>H:MM:SS(.mmm)</c>／<c>MM:SS(.mmm)</c>／純秒 → 秒；容忍前後雜字元（取首個時鐘樣式）。空／解不出→null（時間未知）。</summary>
    public static double? ParseFlexibleTime(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) { return null; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var m = ClockRe.Match(t);
        if (m.Success)
        {
            if (m.Groups["h"].Success)
            {
                return int.Parse(m.Groups["h"].Value, inv) * 3600 + int.Parse(m.Groups["m"].Value, inv) * 60
                    + double.Parse(m.Groups["s"].Value, System.Globalization.NumberStyles.Float, inv);
            }
            return int.Parse(m.Groups["m2"].Value, inv) * 60 + double.Parse(m.Groups["s2"].Value, System.Globalization.NumberStyles.Float, inv);
        }
        return double.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, inv, out var sec) && sec >= 0 ? sec : (double?)null;
    }

    /// <summary>
    /// 判斷 Responses 回應是否因輸出上限被截斷（純函式，增量5′ 審查修）：頂層 <c>status=="incomplete"</c>
    /// 或 <c>incomplete_details.reason=="max_output_tokens"</c>＝輸出未完成、內容不可靠（截斷之 JSON 解析後多為空清單）。
    /// 呼叫端據此給明確「內容過長」錯誤而非靜默回空、誤指 URL、反覆付費重試。信封非 JSON／無 status → false。
    /// </summary>
    public static bool IsTruncated(string? responsesApiJson)
    {
        if (string.IsNullOrWhiteSpace(responsesApiJson)) { return false; }
        try
        {
            using var doc = JsonDocument.Parse(responsesApiJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return false; }
            if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                && string.Equals(st.GetString(), "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return root.TryGetProperty("incomplete_details", out var id) && id.ValueKind == JsonValueKind.Object
                && id.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String
                && string.Equals(rs.GetString(), "max_output_tokens", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>自模型輸出文字取 JSON 物件：去 <c>```</c> 圍籬、取首個 '{' 到末個 '}'；空／無物件回 null。</summary>
    private static string? ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) { return null; }
        var s = content;
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var nl = s.IndexOf('\n', fence);
            var close = nl >= 0 ? s.IndexOf("```", nl + 1, StringComparison.Ordinal) : -1;
            if (nl >= 0 && close > nl) { s = s.Substring(nl + 1, close - nl - 1); }
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : null;
    }
}
