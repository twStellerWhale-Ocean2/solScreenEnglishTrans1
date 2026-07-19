using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoIsland.Video;

/// <summary>
/// 字幕解析（[techItem字幕擷取]／[modVideoCapture模組] 影片擷取契約，spec#2）：把 VTT／SRT 字幕文字
/// 解析為逐句 <see cref="SubtitleCue"/>（文字＋起訖秒）。不依賴 UI／網路、純函式、可單元測試。
/// 容錯：時間軸可含或不含小時位、逗號或句點毫秒分隔；剝除 VTT／HTML 標籤（<c>、行內 &lt;00:00:01.500&gt; 等）
/// 與常見 HTML 實體；多行字幕以空白併行；去除連續完全重複之字幕（YouTube 自動字幕滾動重複）；空文字略過。
/// </summary>
public static class SubtitleParser
{
    private static readonly Regex TimeLine = new(
        @"(?<a>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{3})\s*-->\s*(?<b>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{3})",
        RegexOptions.Compiled);
    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    // VTT 語音標記 <v Speaker>／<v.loud Speaker>（可帶 .class）——擷取說話人名（epic #145 增量5）。於 Tag 剝除前先取。
    private static readonly Regex VoiceTag = new(@"<v(?:\.[^\s>]+)*\s+(?<who>[^>]+)>", RegexOptions.Compiled);

    /// <summary>解析字幕全文為逐句 cue（依出現順序、去連續重複、空文字略過）。null／無時間軸回空清單。</summary>
    public static IReadOnlyList<SubtitleCue> Parse(string? content)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(content)) return cues;

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var m = TimeLine.Match(lines[i]);
            if (!m.Success) { i++; continue; }
            var start = ParseTime(m.Groups["a"].Value);
            var end = ParseTime(m.Groups["b"].Value);
            i++;

            var sb = new StringBuilder();
            string? speaker = null; // 取本 cue 首個 <v Speaker> 語音標記之說話人（有則用，多數字幕無）
            while (i < lines.Length && lines[i].Trim().Length > 0 && !TimeLine.IsMatch(lines[i]))
            {
                if (speaker is null)
                {
                    var vm = VoiceTag.Match(lines[i]);
                    if (vm.Success) speaker = vm.Groups["who"].Value.Trim();
                }
                var clean = Clean(lines[i]);
                if (clean.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(clean);
                }
                i++;
            }

            var text = Ws.Replace(sb.ToString(), " ").Trim();
            if (text.Length == 0 || end <= start) continue;                                // end 僅用於濾零長/反向 cue，不入 start-only 模型
            if (cues.Count > 0)
            {
                var prev = cues[^1];
                if (text == prev.Text) continue;                                           // 完全重複
                if (text.StartsWith(prev.Text, StringComparison.Ordinal))                  // 滾動延伸（後句含前句）→ 以較完整者取代（start-only：僅換文字、保留起點）
                {
                    cues[^1] = prev with { Text = text, Speaker = prev.Speaker ?? speaker };
                    continue;
                }
                if (prev.Text.StartsWith(text, StringComparison.Ordinal)) continue;        // 為前句之較短前綴 → 略過
            }
            cues.Add(new SubtitleCue(text, start, speaker));
        }
        return cues;
    }

    /// <summary>
    /// 依開始時間**穩定排序**,使 cue 序單調遞增（epic #178 增量6′-B「時間 pivot」修）：字幕檔／逐字稿頁常見場景倒敘、
    /// 片尾曲另計時等,致解析出的句序時間**不遞增**——如 fandom transcript 實測出現 875→730、1350→1262 之回退段。
    /// <see cref="PauseDecider"/>（<c>NextPause</c>／<c>CueAt</c>）明文要求 cues 依 <see cref="SubtitleCue.StartSec"/> 遞增;違反時
    /// 「指定說話人暫停」會在回退段整批句被瞬間掃過（不逐句停）——USR 回報「選 Ryder 但沒每次停」即此。
    /// 未定時句（<c>StartSec==null</c>）以「前一已定時句之時間」為鍵、隨其相鄰（不因無時間飄到頭尾）;OrderBy 為穩定排序,同鍵保留原順序。
    /// 純函式、對已遞增輸入為 idempotent、供單元測試。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> NormalizeOrder(IReadOnlyList<SubtitleCue> cues)
    {
        if (cues is null || cues.Count < 2) return cues ?? new List<SubtitleCue>();
        var keyed = new (SubtitleCue Cue, double Key, int Orig)[cues.Count];
        var carry = 0.0; // 未定時句承接前一已定時句時間→排序時黏在其後,不因無時間被推到頭/尾
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].StartSec is double s) carry = s;
            keyed[i] = (cues[i], carry, i);
        }
        // OrderBy 穩定:同鍵（同秒、或承接同一時間之未定時句）保留原始相對順序;ThenBy(Orig) 明示化、防未來不穩定實作。
        return keyed.OrderBy(k => k.Key).ThenBy(k => k.Orig).Select(k => k.Cue).ToList();
    }

    /// <summary>
    /// 補抽「<c>NAME: 台詞</c>」行首前綴之說話人（epic #178 增量6′-B「時間 pivot」定案——字幕檔自帶時間＋說話人、直接載入）：
    /// 除 VTT <c>&lt;v Name&gt;</c> 外,字幕檔常以行首「<c>Ryder: …</c>」「<c>CAP'N TURBOT: …</c>」標說話人。
    /// 僅在該句**尚無說話人**（未由 <c>&lt;v&gt;</c> 取得）時套用;名字須**大寫開頭、≤3 詞、≤24 字、不含逗號等句子標點**,避免把「Well: …」誤判成說話人。純函式、internal 供單元測試。
    /// </summary>
    internal static IReadOnlyList<SubtitleCue> ExtractInlineSpeakers(IReadOnlyList<SubtitleCue> cues)
    {
        // 名字後容忍冒號前空白（表格逐字稿常「Speaker : 台詞」）。\s* 在冒號前。
        var re = new Regex(@"^(?<who>\p{Lu}[\p{L}\p{M}0-9'.\- ]{0,22}[\p{L}0-9.])\s*:\s+(?<line>\S.*)$", RegexOptions.Compiled);
        var result = new List<SubtitleCue>(cues.Count);
        foreach (var c in cues)
        {
            if (!string.IsNullOrWhiteSpace(c.Speaker)) { result.Add(c); continue; } // 已由 <v> 取得說話人→不動
            var m = re.Match(c.Text);
            result.Add(m.Success && CountWords(m.Groups["who"].Value) <= 3
                ? c with { Speaker = m.Groups["who"].Value.Trim(), Text = m.Groups["line"].Value.Trim() }
                : c);
        }
        return result;
    }

    /// <summary>
    /// 解析「時間戳＋說話人」逐字稿頁（epic #178 增量6′-B「時間 pivot」定案；如 fandom transcript：HTML 表格,每列 <c>HH:MM:SS</c> ＋ <c>Speaker:</c> ＋ 台詞）:
    /// 去 HTML 標籤→純文字→解實體→以每個 <c>HH:MM:SS</c> 為一句起點,擷取其後至下一時間戳之文字（說話人由 <see cref="ExtractInlineSpeakers"/> 補抽）。
    /// 非 VTT/SRT 之「-->」箭頭格式,故 <see cref="Parse"/> 讀不到時間時由呼叫端 fallback 用此。末句常吞入頁尾雜訊→以長度上限截斷保底。純函式、internal 供單元測試。
    /// </summary>
    internal static IReadOnlyList<SubtitleCue> ParseTimedTranscript(string? content)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(content)) return cues;
        var text = System.Net.WebUtility.HtmlDecode(Tag.Replace(content, " "));
        foreach (Match m in Regex.Matches(text, @"(?<t>\d{1,2}:\d{2}:\d{2})(?<body>.*?)(?=\d{1,2}:\d{2}:\d{2}|$)", RegexOptions.Singleline))
        {
            var body = Ws.Replace(m.Groups["body"].Value, " ").Trim();
            if (body.Length == 0) continue;
            if (body.Length > 300) { body = body[..300].Trim(); } // 末句常吞入頁尾雜訊（無下一時間戳收界）→截斷保底,使用者可 Edit YAML 修
            cues.Add(new SubtitleCue(body, ParseTime(m.Groups["t"].Value)));
        }
        return cues;
    }

    /// <summary>
    /// 解析 YouTube <c>json3</c> 字幕（[techItem字幕擷取]，spec#2）為含結束時間之 <see cref="TimedCue"/>（供 <see cref="CoalesceCues"/> 之間隔判斷）。
    /// json3 為<b>事件級</b>結構（非 VTT 之逐字滾動渲染），自動字幕改抓此格式即乾淨、無滾動重複：
    /// 每個 <c>event</c>（含 <c>segs[].utf8</c> 與 <c>tStartMs</c>／<c>dDurationMs</c>）→ 一句 cue。
    /// 空文字／缺時間之 event 略過；去連續完全重複；malformed／null 回空清單、不擲例外。
    /// start-only（#158）：結束時間僅內部（TimedCue）保留供併句，最終併合輸出對外 <see cref="SubtitleCue"/> 時丟棄。internal 供單元測試。
    /// </summary>
    internal static IReadOnlyList<TimedCue> ParseJson3Timed(string? content)
    {
        var cues = new List<TimedCue>();
        if (string.IsNullOrWhiteSpace(content)) return cues;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Array)
            {
                return cues;
            }
            foreach (var ev in events.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.Object
                    || !ev.TryGetProperty("segs", out var segs) || segs.ValueKind != JsonValueKind.Array
                    || !ev.TryGetProperty("tStartMs", out var tStart))
                {
                    continue;
                }
                var startMs = ReadNum(tStart);
                var durMs = ev.TryGetProperty("dDurationMs", out var d) ? ReadNum(d) : 0;

                var sb = new StringBuilder();
                foreach (var seg in segs.EnumerateArray())
                {
                    if (seg.ValueKind == JsonValueKind.Object
                        && seg.TryGetProperty("utf8", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(u.GetString());
                    }
                }
                var text = Ws.Replace(sb.ToString(), " ").Trim();
                if (text.Length == 0) continue;

                var start = startMs / 1000.0;
                var end = (startMs + durMs) / 1000.0;
                if (end <= start) end = start + 0.1; // 保底：零長 event 給極短區間，供併句間隔判斷

                if (cues.Count > 0 && cues[^1].Text == text) continue; // 去連續完全重複
                cues.Add(new TimedCue(text, start, end));
            }
        }
        catch (JsonException)
        {
            // malformed json3 → 回目前已解析（多半空），不擲例外中斷上層。
        }
        return cues;
    }

    /// <summary>json3 數值欄位可能為 number 或字串化數字，皆容錯讀為 double（毫秒）。</summary>
    private static double ReadNum(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetDouble()
        : double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>
    /// 併合過細之相鄰 cue 為句級（json3 事件級常過碎，如單字「shovel.」「Incoming.」——到句暫停過頻）：
    /// 累積相鄰 cue，遇下列任一即斷句起新句——目前句已以句末標點（<c>. ? ! …</c>）結束、與下一 cue 時間間隔過大
    /// （&gt; <paramref name="maxGapSec"/>）、下一 cue 以換說話者標記 <c>&gt;&gt;</c> 起始、下一 cue 帶不同<b>具名說話人</b>
    /// （<see cref="SubtitleCue.Speaker"/>，epic #145 增量5——不同人不併句）、或目前句已達字數上限（&gt;= <paramref name="maxWords"/>）。
    /// 保留首 cue 起點、併句沿用首 cue 說話人；輸入為含結束時間之 <see cref="TimedCue"/>（間隔＝下一 cue 起點－目前累積訖點），
    /// 輸出對外 start-only <see cref="SubtitleCue"/>（#158，丟棄結束時間）。純函式、internal 供單元測試。
    /// </summary>
    internal static IReadOnlyList<SubtitleCue> CoalesceCues(
        IReadOnlyList<TimedCue> cues, int maxWords = 14, double maxGapSec = 1.2)
    {
        var result = new List<SubtitleCue>();
        TimedCue? cur = null;
        foreach (var cue in cues)
        {
            if (cur is null) { cur = cue; continue; }
            var gap = cue.StartSec - cur.EndSec;
            var startsNewSpeaker = cue.Text.StartsWith(">>", StringComparison.Ordinal);
            var speakerChange = !string.IsNullOrEmpty(cue.Speaker)
                && !string.Equals(cue.Speaker, cur.Speaker, StringComparison.OrdinalIgnoreCase);
            if (EndsSentence(cur.Text) || gap > maxGapSec || CountWords(cur.Text) >= maxWords
                || startsNewSpeaker || speakerChange)
            {
                result.Add(new SubtitleCue(cur.Text, cur.StartSec, cur.Speaker)); // 輸出丟棄 end
                cur = cue;
            }
            else
            {
                cur = cur with { Text = cur.Text + " " + cue.Text, EndSec = cue.EndSec }; // 沿用 cur.Speaker、延長 end 供下一間隔
            }
        }
        if (cur is not null) result.Add(new SubtitleCue(cur.Text, cur.StartSec, cur.Speaker));
        return result;
    }

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        return t.Length > 0 && t[^1] is '.' or '?' or '!' or '…';
    }

    private static int CountWords(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Clean(string s)
    {
        s = Tag.Replace(s, "");
        s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
             .Replace("&#39;", "'").Replace("&quot;", "\"").Replace("&nbsp;", " ");
        return s.Trim();
    }

    /// <summary>解析 <c>HH:MM:SS.mmm</c>／<c>MM:SS.mmm</c>（逗號或句點毫秒）為秒。internal 供測試。</summary>
    internal static double ParseTime(string t)
    {
        t = t.Replace(',', '.');
        var parts = t.Split(':');
        double h = 0, min, sec;
        if (parts.Length == 3)
        {
            h = double.Parse(parts[0], CultureInfo.InvariantCulture);
            min = double.Parse(parts[1], CultureInfo.InvariantCulture);
            sec = double.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        else
        {
            min = double.Parse(parts[0], CultureInfo.InvariantCulture);
            sec = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        return h * 3600 + min * 60 + sec;
    }
}
