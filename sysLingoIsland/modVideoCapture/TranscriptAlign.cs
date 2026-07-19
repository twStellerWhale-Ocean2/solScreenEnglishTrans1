using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 字幕主線 pivot 之純函式（[modVideoCapture模組]，epic #178 增量5′）：字幕檔整理（去 HTML、組解析提示、解析逐句序列）與
/// 逐句對齊（組聲音時間軸、組對齊提示、解析每句時間、組裝帶說話人＋時間之 cue）。不依賴網路／UI，可單元測試；
/// HTTP 由 <see cref="OpenAiTranscriptAligner"/> 負責。回應解析沿用 <see cref="SpeakerInference.ExtractOutputText"/>（Responses 信封取模型文字）。
/// </summary>
public static class TranscriptAlign
{
    /// <summary>對齊分塊大小（每塊台詞句數）：逐塊小而準、輸出長度可控。</summary>
    public const int ChunkSize = 40;

    private static readonly Regex Parenthetical = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled); // 一組 (...) 或 [...] 舞台指示／音效
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

    // ── 第1段：字幕檔整理（原文→逐句「說話人＋台詞」序列） ─────────────────────────

    /// <summary>組「整理字幕檔為逐句序列」提示（不上網）：擷取依序對白、每句標說話人（角色名或空）、略過雜訊；回 <c>{lines:[{speaker,text}]}</c>。</summary>
    public static string BuildParsePrompt(string transcriptText)
    {
        var sb = new StringBuilder();
        sb.Append("下面是一支英文影片的字幕檔／逐字稿原文（可能夾雜網頁導覽、標題、廣告等雜訊）。請擷取其中**依序的對白**，整理成逐句序列。\n");
        sb.Append("每句判斷其**說話者（角色名）**：若原文以「角色：台詞」「角色 - 台詞」等標明，取該角色名；無明確角色則說話者留空字串。\n");
        sb.Append("略過：導覽列／頁尾／廣告／章節標題／集數資訊／純舞台指示（場景描述、[music]、(applause) 等）等非對白。保持台詞原文與出現順序，不要翻譯、不要改寫。\n");
        sb.Append("只回傳 JSON：{\"lines\":[{\"speaker\":\"角色名或空字串\",\"text\":\"台詞\"}, ...]}。不要輸出任何說明文字。\n\n");
        sb.Append("原文：\n---\n").Append(transcriptText.Trim()).Append("\n---");
        return sb.ToString();
    }

    /// <summary>解析「整理字幕檔」之 Responses 回應為逐句 <see cref="TranscriptLine"/>（取 output_text 內之 <c>{lines:[{speaker,text}]}</c>；容忍圍籬與前後贅字）；空／缺欄／解析失敗回空清單。台詞空白之句略過；說話人空白＝未標示（null）。</summary>
    public static IReadOnlyList<TranscriptLine> ParseLines(string responsesApiJson)
    {
        var lines = new List<TranscriptLine>();
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = SpeakerInference.ExtractOutputText(doc.RootElement);
        var json = ExtractJsonObject(text);
        if (json is null) { return lines; }
        try
        {
            using var inner = JsonDocument.Parse(json);
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("lines", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return lines;
            }
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) { continue; }
                var lineText = (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)?.Trim();
                if (string.IsNullOrEmpty(lineText)) { continue; }
                if (IsPureNonSpeech(lineText)) { continue; } // 純舞台指示／音效（如「(Sighing)」「(Bird squawking)」）——非口說、無學習價值、且會誤配聲音段致時間反轉；丟棄
                var speaker = (el.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null)?.Trim();
                lines.Add(new TranscriptLine(string.IsNullOrEmpty(speaker) ? null : speaker, lineText));
            }
        }
        catch (JsonException) { /* malformed → 回已解析部分（多半空） */ }
        return lines;
    }

    /// <summary>
    /// 是否為**純非口說**之句（純函式，增量5′ 精度修）：去除所有 <c>(...)</c>／<c>[...]</c> 舞台指示／音效後若無任何字母數字殘留＝非台詞
    /// （如「(Sighing)」「(Bird squawking)」「[music]」）→ 丟棄（無學習價值、且會誤配聲音段致時間反轉）。含實際台詞者（如「(Gasping) Bingo!」）保留。空白亦視為非口說。
    /// </summary>
    public static bool IsPureNonSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return true; }
        var stripped = Parenthetical.Replace(text, " ");
        return !stripped.Any(char.IsLetterOrDigit);
    }

    // ── 第2段：對齊（字幕檔句 ↔ Whisper 聲音時間軸） ────────────────────────────

    /// <summary>可用之 Whisper 聲音段（有時間＋非空文字）——對齊之編號基準（純函式）：渲染與「編號→時間」查表用**同一份**，確保編號對應一致。Whisper 段本即全定時非空，此為防呆過濾。</summary>
    public static IReadOnlyList<SubtitleCue> UsableAudioSegments(IReadOnlyList<SubtitleCue> audioCues)
        => audioCues.Where(c => c.StartSec.HasValue && !string.IsNullOrWhiteSpace(c.Text)).ToList();

    /// <summary>
    /// 把（已由 <see cref="UsableAudioSegments"/> 篩之）Whisper 聲音段渲染為**已編號**對齊參考（每行 <c>[i] 聽寫文字</c>，1-based、依時間遞增）。純函式。
    /// <b>刻意不顯示秒數</b>——增量5′ 精度修：讓模型只挑「對應段編號」而非估算時間（估算有 ±數秒誤差），時間之後由 <see cref="MapRefsToTimes"/> 取該段之 Whisper 精確時間。
    /// </summary>
    public static string RenderAudioTimeline(IReadOnlyList<SubtitleCue> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").Append((segments[i].Text ?? "").Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 組「把每句台詞對應到聲音段**編號**」提示（不上網，增量5′ 精度修）：不要模型估算時間，而是挑出該台詞在聲音中念出的**段編號**——
    /// 時間之後取該段之 Whisper 精確時間，精度＝Whisper 本身。給已編號聲音段與該塊台詞，回每句對應段編號（恰該塊句數、對不到回 -1、單調不遞減）。
    /// </summary>
    public static string BuildAlignPrompt(IReadOnlyList<TranscriptLine> chunk, string numberedTimeline)
    {
        var sb = new StringBuilder();
        sb.Append("以下是一支影片**聲音轉錄的逐句內容**（每行『[編號] 聽寫文字』，依時間先後、已編號）：\n---\n");
        sb.Append(numberedTimeline.Trim()).Append("\n---\n");
        sb.Append("下面是該影片**字幕檔的其中 ").Append(chunk.Count).Append(" 句台詞**（已編號、依敘事順序）。請**對照上面的聲音逐句**，判斷每一句台詞是在**哪一個聲音編號**開始念出的（該台詞開頭字詞出現的那段）。\n");
        sb.Append("規則：台詞文字與聽寫文字可能不完全一致（用詞／標點／大小寫），以語意最相近者對齊；一句台詞跨多段時取**開頭那段**之編號；聲音編號須隨台詞編號**單調不遞減**；實在對不到聲音者回 -1（寧可留 -1 勿硬填）。\n");
        sb.Append("只回傳 JSON：{\"refs\":[...]}，refs 長度恰好 ").Append(chunk.Count).Append(" 個、依序對應、每值為聲音編號（正整數）或 -1。不要輸出任何說明文字。\n\n台詞：");
        for (var i = 0; i < chunk.Count; i++) { sb.Append('\n').Append(i + 1).Append(". ").Append(chunk[i].Text); }
        return sb.ToString();
    }

    /// <summary>解析「對齊」之 Responses 回應為每句對應之聲音段**編號**（1-based；取 output_text 內之 <c>{refs:[...]}</c>）：長度校正為 <paramref name="expectedCount"/>（短補 null、長截斷）；&lt;1／非整數／缺→null（對不到）。</summary>
    public static IReadOnlyList<int?> ParseRefs(string responsesApiJson, int expectedCount)
    {
        var result = new int?[Math.Max(0, expectedCount)];
        using var doc = JsonDocument.Parse(responsesApiJson);
        var text = SpeakerInference.ExtractOutputText(doc.RootElement);
        var json = ExtractJsonObject(text);
        if (json is null) { return result; }
        try
        {
            using var inner = JsonDocument.Parse(json);
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("refs", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            var i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                if (i >= result.Length) { break; }           // 長於預期→截斷
                result[i] = ReadRef(el);
                i++;
            }
        }
        catch (JsonException) { /* malformed → 回已解析部分（其餘 null） */ }
        return result;
    }

    /// <summary>單一段編號容錯讀取：正整數→該值；字串化正整數→值；其餘（-1、0、非整數、null）→null（對不到）。</summary>
    private static int? ReadRef(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) { return v >= 1 ? v : (int?)null; }
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var sv)) { return sv >= 1 ? sv : (int?)null; }
        return null;
    }

    /// <summary>
    /// 把每句對應之聲音段編號（1-based）映射為**該段之 Whisper 精確開始秒**（純函式，增量5′ 精度修）：編號越界／null→null（時間未知）。
    /// <b>時間精度＝Whisper 段時間、非模型估算</b>——修正「AI 估算時間 ±數秒抖動」。<paramref name="segments"/> 須為 <see cref="UsableAudioSegments"/> 之結果（與渲染同一份）。
    /// </summary>
    public static IReadOnlyList<double?> MapRefsToTimes(IReadOnlyList<int?> refs, IReadOnlyList<SubtitleCue> segments)
    {
        var times = new double?[refs.Count];
        for (var i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            times[i] = (r.HasValue && r.Value >= 1 && r.Value <= segments.Count) ? segments[r.Value - 1].StartSec : null;
        }
        return times;
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

    // ── 組裝 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 把整理後之逐句（說話人＋台詞）與對齊所得每句開始秒組裝為 <see cref="SubtitleCue"/>（純函式）：
    /// 依 <paramref name="lines"/> **敘事順序**（逐字稿為主、閱讀序），時間取 <paramref name="startSecs"/> 對應值（缺／null＝時間未知，#184）。
    /// <b>不重排序</b>——維持字幕檔敘事序（含說話人序列完整性）；未定時句留原位、由 <see cref="PauseDecider"/> 之 null 容忍略過（增量4）。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Assemble(IReadOnlyList<TranscriptLine> lines, IReadOnlyList<double?> startSecs)
    {
        var cues = new List<SubtitleCue>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var time = i < startSecs.Count ? startSecs[i] : null;
            var speaker = string.IsNullOrWhiteSpace(lines[i].Speaker) ? null : lines[i].Speaker!.Trim();
            cues.Add(new SubtitleCue(lines[i].Text, time, speaker));
        }
        return cues;
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
