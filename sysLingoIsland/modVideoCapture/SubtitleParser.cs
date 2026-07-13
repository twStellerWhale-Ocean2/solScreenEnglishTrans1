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
            while (i < lines.Length && lines[i].Trim().Length > 0 && !TimeLine.IsMatch(lines[i]))
            {
                var clean = Clean(lines[i]);
                if (clean.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(clean);
                }
                i++;
            }

            var text = Ws.Replace(sb.ToString(), " ").Trim();
            if (text.Length == 0 || end <= start) continue;
            if (cues.Count > 0)
            {
                var prev = cues[^1];
                if (text == prev.Text) continue;                                           // 完全重複
                if (text.StartsWith(prev.Text, StringComparison.Ordinal))                  // 滾動延伸（後句含前句）→ 以較完整者取代、延長結束時間
                {
                    cues[^1] = prev with { Text = text, EndSec = end };
                    continue;
                }
                if (prev.Text.StartsWith(text, StringComparison.Ordinal)) continue;        // 為前句之較短前綴 → 略過
            }
            cues.Add(new SubtitleCue(text, start, end));
        }
        return cues;
    }

    /// <summary>
    /// 解析 YouTube <c>json3</c> 字幕（[techItem字幕擷取]，spec#2）為逐句 <see cref="SubtitleCue"/>。
    /// json3 為<b>事件級</b>結構（非 VTT 之逐字滾動渲染），自動字幕改抓此格式即乾淨、無滾動重複：
    /// 每個 <c>event</c>（含 <c>segs[].utf8</c> 與 <c>tStartMs</c>／<c>dDurationMs</c>）→ 一句 cue。
    /// 空文字／缺時間之 event 略過；去連續完全重複；malformed／null 回空清單、不擲例外。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> ParseJson3(string? content)
    {
        var cues = new List<SubtitleCue>();
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
                if (end <= start) end = start + 0.1; // 保底：零長 event 給極短區間，供到句暫停

                if (cues.Count > 0 && cues[^1].Text == text) continue; // 去連續完全重複
                cues.Add(new SubtitleCue(text, start, end));
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
    /// （&gt; <paramref name="maxGapSec"/>）、下一 cue 以換說話者標記 <c>&gt;&gt;</c> 起始、或目前句已達字數上限
    /// （&gt;= <paramref name="maxWords"/>）。保留首 cue 起點、末 cue 訖點。純函式、internal 供單元測試。
    /// </summary>
    internal static IReadOnlyList<SubtitleCue> CoalesceCues(
        IReadOnlyList<SubtitleCue> cues, int maxWords = 14, double maxGapSec = 1.2)
    {
        var result = new List<SubtitleCue>();
        SubtitleCue? cur = null;
        foreach (var cue in cues)
        {
            if (cur is null) { cur = cue; continue; }
            var gap = cue.StartSec - cur.EndSec;
            var startsNewSpeaker = cue.Text.StartsWith(">>", StringComparison.Ordinal);
            if (EndsSentence(cur.Text) || gap > maxGapSec || CountWords(cur.Text) >= maxWords || startsNewSpeaker)
            {
                result.Add(cur);
                cur = cue;
            }
            else
            {
                cur = cur with { Text = cur.Text + " " + cue.Text, EndSec = cue.EndSec };
            }
        }
        if (cur is not null) result.Add(cur);
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
